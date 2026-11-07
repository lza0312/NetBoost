using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NetBoost.Core;

/// <summary>
/// IP 质量评估：TCP 握手往返时间（RTT）+ TLS 证书校验。
/// 证书校验很关键——它能过滤掉能连通但并不是目标站点的劫持 IP。
/// </summary>
public static class IpBenchmark
{
    private const int Port = 443;

    /// <summary>
    /// 全局并发闸门。多个域名同时检测时，若每个域名各自开 16 个连接，
    /// 总并发会瞬间超过家用网络的承受能力，导致大量本可连通的 IP 被误判为超时。
    /// </summary>
    private static readonly SemaphoreSlim TcpGate = new(24);

    /// <summary>
    /// TLS 握手并发：自适应 CPU 核数（4~12），也可用环境变量
    /// NETBOOST_TLS_CONCURRENCY 覆盖，避免在弱机上把网络压垮或在强机上浪费等待。
    /// </summary>
    private static readonly SemaphoreSlim TlsGate = new(TlsConcurrency());

    private static int TlsConcurrency()
    {
        var custom = Environment.GetEnvironmentVariable("NETBOOST_TLS_CONCURRENCY");
        if (int.TryParse(custom, out var value) && value is >= 1 and <= 32) return value;

        return Math.Clamp(Environment.ProcessorCount, 4, 12);
    }

    /// <summary>
    /// 并发测量一批 IP 的 TCP 连接延迟（毫秒），不可达的 IP 不会出现在结果里。
    /// 某个 IP 一旦连接失败就立即放弃（TCP 超时已说明不可达，重试只是浪费时间）；
    /// 成功时多次采样取平均，比取最小值更能反映稳定性。
    /// </summary>
    public static async Task<Dictionary<string, double>> MeasureManyAsync(
        IReadOnlyCollection<string> ips, int samples = 2, int timeoutMs = 2000, int minSuccess = 1)
    {
        var results = new Dictionary<string, double>(StringComparer.Ordinal);
        if (ips.Count == 0) return results;

        var tasks = ips.Distinct(StringComparer.Ordinal).Select(async ip =>
        {
            await TcpGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var success = 0;
                var total = 0d;
                for (var i = 0; i < samples; i++)
                {
                    var ms = await MeasureOnceAsync(ip, Port, timeoutMs).ConfigureAwait(false);
                    if (ms is null) { success = 0; break; } // 不可达，立即放弃
                    success++;
                    total += ms.Value;
                }

                if (success >= minSuccess)
                {
                    lock (results) results[ip] = total / success;
                }
            }
            finally
            {
                TcpGate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static async Task<double?> MeasureOnceAsync(string ip, int port, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验该 IP 上 443 端口的 TLS 证书是否确实签发给 <paramref name="host"/>。
    /// </summary>
    public static async Task<bool> IsCertificateValidAsync(string ip, string host, int timeoutMs = 3500)
    {
        await TlsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, Port, cts.Token).ConfigureAwait(false);

            var errors = SslPolicyErrors.RemoteCertificateNotAvailable;
            var sawCertificate = false;

            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, err) =>
            {
                sawCertificate = cert is not null;
                errors = err;
                return true; // 只记录结果，不在这里中断握手
            });

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host },
                cts.Token).ConfigureAwait(false);

            return sawCertificate && errors == SslPolicyErrors.None;
        }
        catch
        {
            return false;
        }
        finally
        {
            TlsGate.Release();
        }
    }
}
