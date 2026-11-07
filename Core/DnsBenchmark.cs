using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetBoost.Core;

public sealed class DnsProbeOutcome
{
    public bool Responded { get; init; }
    public double LatencyMs { get; init; }
    public int RCode { get; init; } = -1;
    public int AnswerCount { get; init; }
    public string FirstAnswer { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>一组 DNS（主 + 备选）在多个域名上的综合测速结果。</summary>
public sealed class DnsPairOutcome
{
    public bool Responded { get; init; }
    public double? PrimaryLatencyMs { get; init; }
    public double? SecondaryLatencyMs { get; init; }
    public int PrimaryAnswered { get; init; }
    public int SecondaryAnswered { get; init; }
    public int DomainCount { get; init; }
    public int RCode { get; init; } = -1;
    public int AnswerCount { get; init; }
    public string FirstAnswer { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// 直接向指定 DNS 服务器发一个 UDP 查询，测的是「解析到出结果」的真实往返时间。
/// 只测 TCP 连通性没有意义——DNS 走 UDP，能连上不代表能解析。
/// </summary>
public static class DnsBenchmark
{
    private const int DnsPort = 53;

    /// <summary>测速域名：一个海外 + 两个国内，综合值更接近日常解析体验。</summary>
    public static readonly IReadOnlyList<string> DefaultDomains =
        new[] { "github.com", "www.baidu.com", "www.qq.com" };

    public static async Task<DnsProbeOutcome> ProbeAsync(
        string serverIp, string domain, int timeoutMs = 1400, int attempts = 2)
    {
        if (!IPAddress.TryParse(serverIp, out var address))
            return new DnsProbeOutcome { Error = "地址无效" };

        DnsProbeOutcome? best = null;
        for (var i = 0; i < attempts; i++)
        {
            var outcome = await QueryOnceAsync(address, domain, timeoutMs).ConfigureAwait(false);
            if (!outcome.Responded) continue;
            if (best is null || outcome.LatencyMs < best.LatencyMs) best = outcome;
        }

        return best ?? new DnsProbeOutcome { Error = "无响应" };
    }

    /// <summary>
    /// 测试一组 DNS：主地址与备选地址分别在多个域名上各测一遍。
    /// 主地址取多域名的平均延迟作为排序依据；备选地址单独测出来只做展示。
    /// 每个域名首次单发，没回音再补测一次（超时翻倍），避免网络抖动误判。
    /// </summary>
    public static async Task<DnsPairOutcome> ProbePairAsync(
        string primary, string secondary, IReadOnlyList<string> domains, int timeoutMs = 1400)
    {
        var primaryTask = ProbeAddressAsync(primary, domains, timeoutMs);
        var secondaryTask = secondary.Length > 0
            ? ProbeAddressAsync(secondary, domains, timeoutMs)
            : Task.FromResult<AddressOutcome?>(null);

        await Task.WhenAll(primaryTask, (Task)secondaryTask!).ConfigureAwait(false);

        var primaryOutcome = primaryTask.Result;
        var secondaryOutcome = secondaryTask.Result;

        return new DnsPairOutcome
        {
            Responded = primaryOutcome.Answered > 0,
            PrimaryLatencyMs = primaryOutcome.AverageMs,
            SecondaryLatencyMs = secondaryOutcome?.AverageMs,
            PrimaryAnswered = primaryOutcome.Answered,
            SecondaryAnswered = secondaryOutcome?.Answered ?? 0,
            DomainCount = domains.Count,
            RCode = primaryOutcome.Best.RCode,
            AnswerCount = primaryOutcome.Best.AnswerCount,
            FirstAnswer = primaryOutcome.AnswerText,
            Error = primaryOutcome.Answered > 0 ? "" : primaryOutcome.Error
        };
    }

    private sealed class AddressOutcome
    {
        public int Answered;
        public double? AverageMs;
        public DnsProbeOutcome Best = new() { Error = "无响应" };
        public string AnswerText = "";
        public string Error = "";
    }

    private static async Task<AddressOutcome> ProbeAddressAsync(
        string ip, IReadOnlyList<string> domains, int timeoutMs)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return new AddressOutcome
            {
                Best = new DnsProbeOutcome { Error = "地址无效" },
                Error = "地址无效"
            };

        var tasks = domains.Select(async domain =>
        {
            var first = await QueryOnceAsync(address, domain, timeoutMs).ConfigureAwait(false);
            if (first.Responded) return (Domain: domain, Outcome: first);

            var retry = await QueryOnceAsync(address, domain, timeoutMs * 2).ConfigureAwait(false);
            return (Domain: domain, Outcome: retry);
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var answered = results.Where(r => r.Outcome.Responded).ToList();

        if (answered.Count == 0)
            return new AddressOutcome
            {
                Best = results[0].Outcome,
                Error = results[0].Outcome.Error
            };

        var fastest = answered.MinBy(r => r.Outcome.LatencyMs)!;
        var answers = answered
            .Where(r => r.Outcome.FirstAnswer.Length > 0)
            .Select(r => r.Outcome.FirstAnswer)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return new AddressOutcome
        {
            Answered = answered.Count,
            AverageMs = answered.Average(r => r.Outcome.LatencyMs),
            Best = fastest.Outcome,
            AnswerText = string.Join(" / ", answers)
        };
    }

    private static async Task<DnsProbeOutcome> QueryOnceAsync(IPAddress server, string domain, int timeoutMs)
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        var query = BuildQuery(domain, id);

        using var udp = new UdpClient();
        udp.Connect(server, DnsPort);

        try
        {
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeoutMs);
            var received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();

            var parsed = Parse(received.Buffer, id);
            if (!parsed.Valid)
                return new DnsProbeOutcome { Error = "响应无效" };

            return new DnsProbeOutcome
            {
                Responded = true,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                RCode = parsed.RCode,
                AnswerCount = parsed.AnswerCount,
                FirstAnswer = parsed.FirstAnswer
            };
        }
        catch (OperationCanceledException)
        {
            return new DnsProbeOutcome { Error = "超时" };
        }
        catch (Exception ex)
        {
            return new DnsProbeOutcome { Error = ex.Message };
        }
    }

    /// <summary>手工构造一个最小的 A 记录查询报文。</summary>
    private static byte[] BuildQuery(string domain, ushort id)
    {
        var packet = new List<byte>(64);

        packet.Add((byte)(id >> 8));
        packet.Add((byte)id);
        packet.Add(0x01); packet.Add(0x00); // RD=1
        packet.Add(0x00); packet.Add(0x01); // QDCOUNT
        packet.Add(0x00); packet.Add(0x00); // ANCOUNT
        packet.Add(0x00); packet.Add(0x00); // NSCOUNT
        packet.Add(0x00); packet.Add(0x00); // ARCOUNT

        foreach (var label in domain.Split('.'))
        {
            packet.Add((byte)label.Length);
            foreach (var ch in Encoding.ASCII.GetBytes(label)) packet.Add(ch);
        }
        packet.Add(0x00);

        packet.Add(0x00); packet.Add(0x01); // QTYPE = A
        packet.Add(0x00); packet.Add(0x01); // QCLASS = IN

        return packet.ToArray();
    }

    private static (bool Valid, int RCode, int AnswerCount, string FirstAnswer) Parse(byte[] data, ushort id)
    {
        if (data.Length < 12) return (false, -1, 0, "");

        var responseId = (ushort)((data[0] << 8) | data[1]);
        if (responseId != id) return (false, -1, 0, "");

        var flags = (ushort)((data[2] << 8) | data[3]);
        if ((flags & 0x8000) == 0) return (false, -1, 0, ""); // 不是响应报文

        var rcode = flags & 0x000F;
        var questionCount = (data[4] << 8) | data[5];
        var answerCount = (data[6] << 8) | data[7];

        var pos = 12;
        for (var i = 0; i < questionCount; i++)
        {
            pos = SkipName(data, pos);
            if (pos < 0) return (true, rcode, 0, "");
            pos += 4; // QTYPE + QCLASS
        }

        var found = 0;
        var first = "";
        for (var i = 0; i < answerCount && pos > 0 && pos < data.Length; i++)
        {
            pos = SkipName(data, pos);
            if (pos < 0 || pos + 10 > data.Length) break;

            var type = (data[pos] << 8) | data[pos + 1];
            var length = (data[pos + 8] << 8) | data[pos + 9];
            pos += 10;

            if (pos + length > data.Length) break;

            if (type == 1 && length == 4)
            {
                found++;
                if (first.Length == 0)
                    first = $"{data[pos]}.{data[pos + 1]}.{data[pos + 2]}.{data[pos + 3]}";
            }

            pos += length;
        }

        return (true, rcode, found, first);
    }

    /// <summary>跳过域名（可能含压缩指针），返回下一个字段的偏移。</summary>
    private static int SkipName(byte[] data, int pos)
    {
        while (pos >= 0 && pos < data.Length)
        {
            var length = data[pos];
            if (length == 0) return pos + 1;
            if ((length & 0xC0) == 0xC0) return pos + 2; // 压缩指针
            pos += 1 + length;
        }
        return -1;
    }
}
