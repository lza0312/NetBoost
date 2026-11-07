using System.Diagnostics;
using System.IO;
using System.Text;

namespace NetBoost.Core;

/// <summary>
/// 隐藏参数 --selftest：DoH / TCP / TLS / netsh 各跑一遍，结果写到临时目录日志并以退出码表示成败。
/// 只做只读探测，不会改动 hosts 或 DNS。
/// </summary>
public static class SelfTest
{
    public const string Switch = "--selftest";

    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync()
    {
        var lines = new StringBuilder();
        var failures = 0;

        void Record(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            lines.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        }

        var stamp = DateTime.Now;
        lines.AppendLine($"NetBoost 自检 {stamp:yyyy-MM-dd HH:mm:ss}");
        lines.AppendLine($"OS: {Environment.OSVersion} / CPU {Environment.ProcessorCount} 核 / 管理员: {HostsManager.IsElevated}");
        lines.AppendLine();

        var candidates = new List<DohCandidate>();
        try
        {
            var sw = Stopwatch.StartNew();
            candidates = await DohResolver.QueryAsync("github.com", CancellationToken.None);
            var top = candidates.FirstOrDefault();
            Record("DoH", candidates.Count > 0, candidates.Count > 0
                ? $"{candidates.Count} 个候选，Top {top!.Ip}（{top.Sources} 源 / TTL {top.Ttl}），{sw.ElapsedMilliseconds} ms"
                : "没有解析结果（解析源可能全部不可用）");
        }
        catch (Exception ex)
        {
            Record("DoH", false, ex.Message);
        }

        var ips = candidates.Select(c => c.Ip).Take(3).DefaultIfEmpty("140.82.112.3").ToList();
        var rtts = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var localIps = await DohResolver.LocalAsync("github.com");
            ips = ips.Concat(localIps)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            lines.AppendLine($"本机解析候选: {(localIps.Count > 0 ? string.Join(", ", localIps) : "无")}");
        }
        catch
        {
            // 本机解析失败不影响后续 TCP 测试
        }

        try
        {
            var sw = Stopwatch.StartNew();
            rtts = await IpBenchmark.MeasureManyAsync(ips, samples: 1);
            Record("TCP 443", rtts.Count > 0, rtts.Count > 0
                ? string.Join("，", rtts.Select(kv => $"{kv.Key} {kv.Value:F0} ms")) + $"，{sw.ElapsedMilliseconds} ms"
                : "候选 IP 全部不可达");
        }
        catch (Exception ex)
        {
            Record("TCP 443", false, ex.Message);
        }

        var tlsIp = rtts.OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        try
        {
            if (tlsIp is null)
            {
                Record("TLS", false, "没有可用 IP（TCP 全部失败，跳过 TLS）");
            }
            else
            {
                var sw = Stopwatch.StartNew();
                var valid = await IpBenchmark.IsCertificateValidAsync(tlsIp, "github.com");
                Record("TLS", valid, $"{tlsIp} 证书校验{(valid ? "通过" : "失败")}，{sw.ElapsedMilliseconds} ms");
            }
        }
        catch (Exception ex)
        {
            Record("TLS", false, ex.Message);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var (ok, detail) = await RunNetshProbeAsync();
            Record("netsh", ok, $"{detail}，{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Record("netsh", false, ex.Message);
        }

        lines.AppendLine();
        lines.AppendLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")}（失败 {failures} 项）");

        var path = Path.Combine(Path.GetTempPath(), $"netboost-selftest-{stamp:yyyyMMdd-HHmmss}.log");
        try
        {
            File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 日志写入失败不影响退出码
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<(bool Ok, string Detail)> RunNetshProbeAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("netsh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("interface");
        process.StartInfo.ArgumentList.Add("ipv4");
        process.StartInfo.ArgumentList.Add("show");
        process.StartInfo.ArgumentList.Add("interfaces");

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            return (false, "超时 15 秒未返回");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode == 0)
        {
            var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return (true, "exit 0" + (firstLine.Length > 0 ? $" · {firstLine}" : ""));
        }

        var err = stderr.Trim();
        if (err.Length == 0) err = stdout.Trim();
        return (false, $"exit {process.ExitCode} · {(err.Length > 0 ? err : "无输出")}");
    }
}
