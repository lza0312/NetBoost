using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace NetBoost.Core;

/// <summary>一个 DoH 候选 IP，附带「几个解析源同时返回它」与 TTL。</summary>
public sealed class DohCandidate
{
    public string Ip { get; init; } = "";
    public int Sources { get; init; }
    public int Ttl { get; init; }
}

/// <summary>
/// 通过多个 DoH（DNS over HTTPS）解析服务获取候选 IP。
/// 本地 DNS 常常被污染或缓存过期，多源交叉查询能拿到更真实、更分散的 CDN 节点。
/// 关键优化：DoH 源做熔断。国内访问 Google / Cloudflare 的 DoH 常年不通，
/// 若每个域名都等它们超时，整体耗时会成倍膨胀。
/// </summary>
public static class DohResolver
{
    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan BatchDeadline = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LocalResolveTimeout = TimeSpan.FromSeconds(1.5);

    private static readonly (string Name, string Template)[] Providers =
    {
        ("AliDNS", "https://dns.alidns.com/resolve?name={0}&type=A"),
        ("DNSPod", "https://doh.pub/dns-query?name={0}&type=A"),
        ("Cloudflare", "https://cloudflare-dns.com/dns-query?name={0}&type=A"),
        ("Google", "https://dns.google/resolve?name={0}&type=A")
    };

    /// <summary>某个解析源连续失败后的冷却截止时间。</summary>
    private static readonly Dictionary<string, DateTime> DownUntil = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> FailureCount = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetBoost/1.0");
        return client;
    }

    /// <summary>
    /// 并发查询全部可用 DoH 服务，按「多源一致优先、TTL 次之」返回候选 IP。
    /// 设了整体截止时间：慢的解析源不会拖住整个批次，拿到多少用多少；
    /// 只有真正失败（HTTP 错误 / 解析异常）才计入熔断，单纯超时的慢源不会被打入冷宫。
    /// </summary>
    public static async Task<List<DohCandidate>> QueryAsync(string host, CancellationToken ct)
    {
        var providers = Providers.Where(p => IsAvailable(p.Name)).ToList();
        if (providers.Count == 0) providers = Providers.ToList(); // 全被降级时重试一轮

        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(BatchDeadline);

        var tasks = providers
            .Select(p => (p.Name, Task: QueryOneAsync(p.Template, host, batchCts.Token)))
            .ToList();

        var all = Task.WhenAll(tasks.Select(t => t.Task));
        await Task.WhenAny(all, Task.Delay(BatchDeadline)).ConfigureAwait(false);

        var map = new Dictionary<string, (int Sources, int Ttl)>(StringComparer.Ordinal);
        foreach (var (name, task) in tasks)
        {
            if (task.IsCompletedSuccessfully)
            {
                MarkHealthy(name);
                // 同一来源内去重，避免同一 IP 被计多次
                foreach (var answer in task.Result.DistinctBy(a => a.Ip))
                {
                    var current = map.TryGetValue(answer.Ip, out var value)
                        ? value
                        : (Sources: 0, Ttl: 0);
                    map[answer.Ip] = (current.Sources + 1, Math.Max(current.Ttl, answer.Ttl));
                }
            }
            else if (task.IsFaulted)
            {
                MarkFailure(name);
            }
            // 未完成 / 被批次取消：属于慢源，不记失败
        }

        batchCts.Cancel();

        return map
            .Select(kv => new DohCandidate { Ip = kv.Key, Sources = kv.Value.Sources, Ttl = kv.Value.Ttl })
            .OrderByDescending(c => c.Sources)
            .ThenByDescending(c => c.Ttl)
            .ThenBy(c => c.Ip)
            .ToList();
    }

    private static bool IsAvailable(string name)
    {
        lock (Sync)
        {
            var until = DownUntil.TryGetValue(name, out var value) ? value : DateTime.MinValue;
            return DateTime.UtcNow >= until;
        }
    }

    private static void MarkFailure(string name)
    {
        lock (Sync)
        {
            var count = FailureCount.TryGetValue(name, out var value) ? value : 0;
            count++;
            FailureCount[name] = count;
            if (count >= 2) DownUntil[name] = DateTime.UtcNow + Cooldown;
        }
    }

    private static void MarkHealthy(string name)
    {
        lock (Sync)
        {
            FailureCount[name] = 0;
            DownUntil.Remove(name);
        }
    }

    private static async Task<List<(string Ip, int Ttl)>> QueryOneAsync(string template, string host, CancellationToken ct)
    {
        var url = string.Format(template, Uri.EscapeDataString(host));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerProviderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // 真实失败（HTTP 错误 / 报文异常）抛出，交给 QueryAsync 计入熔断；
        // 单纯超时不算失败，只是这一批先不等它。
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return new List<(string, int)>();

        var list = new List<(string Ip, int Ttl)>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.Number || type.GetInt32() != 1) continue;
            if (!answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) continue;

            var ip = data.GetString();
            if (ip is null || !IsIpv4(ip)) continue;

            var ttl = answer.TryGetProperty("TTL", out var ttlNode) && ttlNode.ValueKind == JsonValueKind.Number
                ? ttlNode.GetInt32()
                : 0;
            list.Add((ip, ttl));
        }
        return list;
    }

    /// <summary>读取本机生效的解析结果（会受 hosts 文件影响），带超时避免被污染域名卡住。</summary>
    public static async Task<List<string>> LocalAsync(string host)
    {
        try
        {
            var probe = Dns.GetHostAddressesAsync(host);
            var timeout = Task.Delay(LocalResolveTimeout);
            var winner = await Task.WhenAny(probe, timeout).ConfigureAwait(false);
            if (winner != probe) return new List<string>();

            var addresses = await probe.ConfigureAwait(false);
            return addresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static bool IsIpv4(string value) =>
        IPAddress.TryParse(value, out var addr) &&
        addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}
