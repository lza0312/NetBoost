using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NetBoost.Core;

public sealed class ProbeResult
{
    public bool Ok { get; init; }
    public double TimeToFirstByteMs { get; init; }
    public double DownloadKbps { get; init; }
    public long Bytes { get; init; }
    public string Error { get; init; } = "";

    public string TtfbText => Ok ? $"{TimeToFirstByteMs:F0} ms" : "失败";
}

/// <summary>
/// 真实 HTTP 探测：首字节时间（TTFB）+ 短时下载速率。
/// 用于对比「优化前 / 优化后」的实际访问速度，比单纯 ping 更贴近体验。
/// </summary>
public static class HttpProbe
{
    /// <summary>单个探测的总预算（秒）。测的是体验，宁可记为超时也不要让界面卡住。</summary>
    public const int DefaultBudgetSeconds = 3;

    public static async Task<ProbeResult> RunAsync(string url, int timeoutSec = DefaultBudgetSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(timeoutSec, 10)),
            PooledConnectionLifetime = TimeSpan.Zero, // 每次新建连接，避免复用干扰对比
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            var sw = Stopwatch.StartNew();
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            // 不校验状态码：即使返回 403/404，能拿到首字节也说明链路可用
            var ttfb = sw.Elapsed.TotalMilliseconds;

            long bytes = 0;
            var downloadSw = Stopwatch.StartNew();
            await using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var buffer = new byte[16 * 1024];
                while (bytes < 512 * 1024 && downloadSw.Elapsed.TotalSeconds < 0.6)
                {
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read <= 0) break;
                    bytes += read;
                }
            }

            var kbps = 0d;
            if (bytes > 0 && downloadSw.Elapsed.TotalMilliseconds > 10)
                kbps = bytes / 1024.0 / downloadSw.Elapsed.TotalSeconds;

            return new ProbeResult
            {
                Ok = true,
                TimeToFirstByteMs = ttfb,
                DownloadKbps = kbps,
                Bytes = bytes
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 在总预算内测多次取更快的值，减少偶发抖动。
    /// 整个调用（含重试）不会超过 <paramref name="budgetSec"/> 秒：
    /// 连不上的服务会在预算耗尽后立刻记为「超时」，不会把界面卡住十几秒。
    /// </summary>
    public static async Task<ProbeResult> RunBestOfAsync(
        string url, int budgetSec = DefaultBudgetSeconds, int attempts = 2)
    {
        var stopwatch = Stopwatch.StartNew();
        ProbeResult? best = null;

        for (var i = 0; i < attempts; i++)
        {
            var remaining = budgetSec - stopwatch.Elapsed.TotalSeconds;
            if (remaining < 1) break; // 剩余时间不够做一次有效探测

            var result = await RunAsync(url, (int)Math.Ceiling(remaining)).ConfigureAwait(false);
            if (!result.Ok)
            {
                if (best is null) return result; // 首次即失败，直接返回
                break;
            }
            if (best is null || result.TimeToFirstByteMs < best.TimeToFirstByteMs) best = result;

            // 重试必须也落在预算内，否则用已有结果收工
            if (budgetSec - stopwatch.Elapsed.TotalSeconds < 1) break;
        }

        return best ?? new ProbeResult { Ok = false, Error = "连接超时" };
    }
}
