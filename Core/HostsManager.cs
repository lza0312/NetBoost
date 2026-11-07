using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace NetBoost.Core;

/// <summary>
/// hosts 文件读写：带标记块、自动备份、冲突项清理、DNS 缓存刷新。
/// </summary>
public static class HostsManager
{
    public const string MarkerStart = "# >>> NetBoost 托管块（自动生成，勿手改） >>>";
    public const string MarkerEnd = "# <<< NetBoost 托管块结束 <<<";

    public static string HostsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "drivers", "etc", "hosts");

    public static string BackupDir { get; } = BackupStore.HostsDir;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern int DnsFlushResolverCache();

    public static void FlushDns()
    {
        try
        {
            if (DnsFlushResolverCache() != 0) return;
        }
        catch
        {
            // 退回命令行方式
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // 忽略
        }
    }

    public static string Backup()
    {
        BackupStore.Ensure();
        Directory.CreateDirectory(BackupDir);
        var target = Path.Combine(BackupDir, $"hosts_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
        File.Copy(HostsPath, target, overwrite: true);
        BackupStore.HardenFile(target);
        return target;
    }

    /// <summary>
    /// 写入优化结果。会先备份，再移除旧的托管块和与之冲突的同名条目。
    /// </summary>
    /// <param name="maps">域名 → IP 的映射集合。</param>
    public static ApplyOutcome Apply(IReadOnlyDictionary<string, string> maps)
    {
        if (maps.Count == 0) return new ApplyOutcome { Written = 0 };

        var backupPath = Backup();
        var original = File.ReadAllText(HostsPath, Encoding.UTF8);
        var transformed = Transform(original, maps);

        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(HostsPath, transformed.Content, utf8);

        return new ApplyOutcome
        {
            BackupPath = backupPath,
            Written = maps.Count,
            RemovedConflicts = transformed.RemovedConflicts
        };
    }

    /// <summary>
    /// 纯文本变换：移除旧托管块 + 冲突的同名条目，再追加新的托管块。
    /// 独立出来便于在不接触系统文件的情况下验证逻辑。
    /// </summary>
    public static HostsTransform Transform(string original, IReadOnlyDictionary<string, string> maps)
    {
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";

        var managedHosts = maps.Keys
            .Select(h => h.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = new List<string>();
        var insideManagedBlock = false;
        var removedConflicts = 0;

        foreach (var rawLine in original.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Contains(MarkerStart, StringComparison.OrdinalIgnoreCase))
            {
                insideManagedBlock = true;
                continue;
            }
            if (line.Contains(MarkerEnd, StringComparison.OrdinalIgnoreCase))
            {
                insideManagedBlock = false;
                continue;
            }
            if (insideManagedBlock) continue;

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                kept.Add(line);
                continue;
            }

            // 与本次托管的域名冲突的手工条目 → 直接移除（已备份可还原）
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && managedHosts.Contains(parts[1]))
            {
                removedConflicts++;
                continue;
            }

            kept.Add(line);
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            kept.RemoveAt(kept.Count - 1);

        var builder = new StringBuilder();
        builder.Append(string.Join(newline, kept));
        builder.Append(newline).Append(newline);
        builder.Append(MarkerStart).Append(newline);
        builder.Append($"# 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}   共 {maps.Count} 条").Append(newline);

        foreach (var (host, ip) in maps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append($"{ip}\t{host}").Append(newline);

        builder.Append(MarkerEnd).Append(newline);

        return new HostsTransform
        {
            Content = builder.ToString(),
            RemovedConflicts = removedConflicts
        };
    }

    /// <summary>
    /// 检查 hosts 是否被第三方工具（SwitchHosts / GitHub520 等）接管。
    /// 这类工具会整体重写 hosts，可能覆盖 NetBoost 的优化结果。
    /// </summary>
    public static string? DetectForeignManager()
    {
        try
        {
            var text = File.ReadAllText(HostsPath, Encoding.UTF8);
            if (text.Contains("SWITCHHOSTS", StringComparison.OrdinalIgnoreCase)) return "SwitchHosts";
            if (text.Contains("GitHub520", StringComparison.OrdinalIgnoreCase)) return "GitHub520";
            if (text.Contains("Update url", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("hosts", StringComparison.OrdinalIgnoreCase)) return "第三方 hosts 同步工具";
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    /// <summary>仅移除托管块，保留手工添加的其他内容。</summary>
    public static bool RemoveManagedBlock()
    {
        var original = File.ReadAllText(HostsPath, Encoding.UTF8);
        if (!original.Contains(MarkerStart, StringComparison.OrdinalIgnoreCase)) return false;

        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var kept = new List<string>();
        var inside = false;

        foreach (var rawLine in original.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Contains(MarkerStart, StringComparison.OrdinalIgnoreCase)) { inside = true; continue; }
            if (line.Contains(MarkerEnd, StringComparison.OrdinalIgnoreCase)) { inside = false; continue; }
            if (!inside) kept.Add(line);
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            kept.RemoveAt(kept.Count - 1);

        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(HostsPath, string.Join(newline, kept) + newline, utf8);
        return true;
    }

    /// <summary>还原最近一次备份。</summary>
    public static string? RestoreLatestBackup()
    {
        if (!Directory.Exists(BackupDir)) return null;

        var latest = Directory.EnumerateFiles(BackupDir, "hosts_*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null) return null;

        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(HostsPath, File.ReadAllText(latest, Encoding.UTF8), utf8);
        return latest;
    }

    public static string? LatestBackup()
    {
        if (!Directory.Exists(BackupDir)) return null;
        return Directory.EnumerateFiles(BackupDir, "hosts_*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}

/// <summary>一次写入的结果。</summary>
public sealed class ApplyOutcome
{
    public string BackupPath { get; init; } = "";
    public int Written { get; init; }
    public int RemovedConflicts { get; init; }
}

/// <summary>纯文本变换的结果。</summary>
public sealed class HostsTransform
{
    public string Content { get; init; } = "";
    public int RemovedConflicts { get; init; }
}
