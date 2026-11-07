using System.Globalization;

namespace NetBoost.Core;

/// <summary>单个域名的检测结果，直接给界面表格用（文本属性已格式化）。</summary>
public sealed class DomainResult
{
    public int Order { get; set; }
    public string ServiceId { get; set; } = "";
    public string Service { get; set; } = "";
    public string Host { get; set; } = "";
    public string Note { get; set; } = "";

    public string OldIp { get; set; } = "";
    public double? OldLatencyMs { get; set; }

    public string NewIp { get; set; } = "";
    public double? NewLatencyMs { get; set; }

    /// <summary>是否勾选写入（默认勾选所有可优化项）。</summary>
    public bool Selected { get; set; }

    /// <summary>本次优化 IP 来自应急兜底表（DNS 不可用时的最后手段）。</summary>
    public bool UsedFallback { get; set; }

    /// <summary>本次是否已经尝试过兜底表（用于区分「没有兜底表」和「兜底也不可用」）。</summary>
    public bool FallbackTried { get; set; }

    /// <summary>是否值得写入优化（延迟降低 15% 以上，或原本不可用现在可用）。</summary>
    public bool Improved =>
        NewLatencyMs.HasValue &&
        NewIp.Length > 0 &&
        (!OldLatencyMs.HasValue || NewLatencyMs.Value < OldLatencyMs.Value * 0.85);

    public bool Applicable => Improved && NewIp != OldIp;

    public string OldIpText => OldIp.Length == 0 ? "—" : OldIp;
    public string OldLatencyText => OldLatencyMs.HasValue ? $"{OldLatencyMs.Value:F0} ms" : "不可用";
    public string NewIpText => NewIp.Length == 0 ? "—" : NewIp;
    public string NewLatencyText => NewLatencyMs.HasValue ? $"{NewLatencyMs.Value:F0} ms" : "—";

    public string ImprovementText
    {
        get
        {
            if (!NewLatencyMs.HasValue) return "—";
            if (!OldLatencyMs.HasValue) return "已恢复";
            if (OldLatencyMs.Value <= 0) return "—";

            var percent = (OldLatencyMs.Value - NewLatencyMs.Value) / OldLatencyMs.Value * 100;
            if (percent < 1) return "≈0%";
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}%", percent);
        }
    }

    public DomainResult Clone() => new()
    {
        Order = Order,
        ServiceId = ServiceId,
        Service = Service,
        Host = Host,
        Note = Note,
        OldIp = OldIp,
        OldLatencyMs = OldLatencyMs,
        NewIp = NewIp,
        NewLatencyMs = NewLatencyMs,
        Selected = Selected,
        UsedFallback = UsedFallback
    };
}

/// <summary>
/// 单个域名的优化决策流程：
/// 本地解析 → 多源 DoH 候选 → TCP 测速 → 证书校验 → 选出最优 IP。
/// </summary>
public static class Detector
{
    private const int MaxCandidates = 10;
    private const int MaxTlsChecks = 5;

    public static async Task<DomainResult> DetectOneAsync(
        string serviceId, string service, string host, int order, CancellationToken ct,
        bool forceRefresh = false)
    {
        var signature = ResultCache.Signature();

        if (!forceRefresh)
        {
            var cached = ResultCache.TryGet(signature, host);
            if (cached is not null)
            {
                cached.ServiceId = serviceId;
                cached.Service = service;
                cached.Host = host;
                cached.Order = order;
                cached.Note = cached.Note.Length > 0 ? cached.Note + " · 缓存" : "缓存结果（5 分钟内）";
                return cached;
            }
        }

        DomainResult result = new()
        {
            ServiceId = serviceId,
            Service = service,
            Host = host,
            Order = order
        };

        DomainResult Finish(DomainResult value)
        {
            // 兜底结果只在本次使用，不写缓存：网络恢复后应立刻回到正常解析路径
            if (!value.UsedFallback && (value.OldIp.Length > 0 || value.NewIp.Length > 0))
                ResultCache.Set(signature, host, value);
            return value;
        }

        // 应急兜底：只有在常规解析/连接/证书校验全都拿不到可用节点时才调用。
        // 兜底 IP 同样要过 TCP 与 TLS 证书校验（SNI 用目标域名），不会放宽安全策略。
        async Task<DomainResult?> TryFallbackAsync(string reason)
        {
            var fallbackIps = KnownIpStore.GetIps(host);
            if (fallbackIps.Count == 0) return null;

            result.FallbackTried = true;
            var fallbackLatency = await IpBenchmark.MeasureManyAsync(fallbackIps).ConfigureAwait(false);
            if (fallbackLatency.Count == 0) return null;

            var orderedFallback = fallbackLatency.OrderBy(kv => kv.Value).Take(3).ToList();
            var checks = await Task.WhenAll(orderedFallback.Select(kv =>
                IpBenchmark.IsCertificateValidAsync(kv.Key, host, TlsTimeoutFor(kv.Value))
            )).ConfigureAwait(false);

            for (var i = 0; i < orderedFallback.Count; i++)
            {
                if (!checks[i]) continue;

                result.NewIp = orderedFallback[i].Key;
                result.NewLatencyMs = orderedFallback[i].Value;
                result.UsedFallback = true;
                result.Note = $"应急兜底（{reason}） · 证书校验通过";
                return Finish(result);
            }

            return null;
        }

        // 1. 并行：本地解析（受 hosts 与本地 DNS 影响）+ 多源 DoH 候选
        var localTask = DohResolver.LocalAsync(host);
        var dohTask = DohResolver.QueryAsync(host, ct);
        await Task.WhenAll(localTask, dohTask).ConfigureAwait(false);

        var localIps = localTask.Result;
        var dohCandidates = dohTask.Result;

        ct.ThrowIfCancellationRequested();

        // 本地 IP 优先进入候选，保证「当前」这一列总能被测到；
        // DoH 候选已按「多源一致 + TTL」排序，越可靠越靠前
        var candidates = localIps
            .Concat(dohCandidates.Select(c => c.Ip))
            .Where(DohResolver.IsIpv4)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        if (candidates.Count == 0)
        {
            var fallback = await TryFallbackAsync("DNS 无法解析").ConfigureAwait(false);
            if (fallback is not null) return fallback;

            result.Note = result.FallbackTried
                ? "未能获取任何候选 IP，兜底 IP 也不可用"
                : "未能获取任何候选 IP";
            return Finish(result);
        }

        // 2. 一次批量测速覆盖本地与候选 IP，避免对同一 IP 测两遍
        var latency = await IpBenchmark.MeasureManyAsync(candidates).ConfigureAwait(false);

        var reachableLocal = localIps.Where(latency.ContainsKey).ToList();
        if (reachableLocal.Count > 0)
        {
            var bestLocal = reachableLocal.MinBy(ip => latency[ip])!;
            result.OldIp = bestLocal;
            result.OldLatencyMs = latency[bestLocal];
        }
        else if (localIps.Count > 0)
        {
            result.OldIp = localIps[0];
            result.Note = "当前解析到的 IP 无法连接";
        }
        else
        {
            result.Note = "本地 DNS 无法解析";
        }

        if (latency.Count == 0)
        {
            var fallback = await TryFallbackAsync("解析结果均无法连接").ConfigureAwait(false);
            if (fallback is not null) return fallback;

            result.Note = result.FallbackTried
                ? "候选 IP 全部超时，兜底 IP 也不可用"
                : "候选 IP 全部超时";
            return Finish(result);
        }

        // 3. 按延迟从快到慢做证书校验（并发），取第一个通过的最优节点
        var ordered = latency.OrderBy(kv => kv.Value).Select(kv => kv.Key).Take(MaxTlsChecks).ToList();
        var validations = await Task.WhenAll(
            ordered.Select(ip => IpBenchmark.IsCertificateValidAsync(ip, host, TlsTimeoutFor(latency[ip])))
        ).ConfigureAwait(false);

        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!validations[i]) continue;

            var ip = ordered[i];
            result.NewIp = ip;
            result.NewLatencyMs = latency[ip];

            var sources = dohCandidates.FirstOrDefault(c => c.Ip == ip)?.Sources ?? 0;
            var reliability = sources > 1 ? $" · {sources} 源一致" : "";

            result.Note = (result.OldIp == ip ? "当前已是最优节点" : "证书校验通过") + reliability;
            return Finish(result);
        }

        // TCP 能通但没有任何节点过得了证书校验（常见于 TLS 被干扰），也属于「实际连不上」
        var tlsFallback = await TryFallbackAsync("候选 IP 未通过证书校验").ConfigureAwait(false);
        if (tlsFallback is not null) return tlsFallback;

        result.Note = result.FallbackTried
            ? "候选 IP 均未通过证书校验，兜底 IP 也不可用"
            : "候选 IP 均未通过证书校验";
        return Finish(result);
    }

    /// <summary>
    /// TLS 握手要 3~4 个往返，高延迟节点 3.5 秒不够用，
    /// 会误判成「证书无效」。按实测 RTT 放宽超时。
    /// </summary>
    private static int TlsTimeoutFor(double latencyMs) =>
        (int)Math.Clamp(latencyMs * 5, 3500, 9000);
}
