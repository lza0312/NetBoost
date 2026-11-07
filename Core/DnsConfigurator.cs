using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace NetBoost.Core;

/// <summary>一块活动网卡的 DNS 现状（IPv4 + IPv6）。</summary>
public sealed class AdapterDnsInfo
{
    public string Name { get; init; } = "";
    public int Index { get; init; } = -1;
    public string AdapterId { get; init; } = "";
    public bool IsDhcp { get; init; }
    public string[] Servers { get; init; } = Array.Empty<string>();
    public bool IsIpv6Dhcp { get; init; }
    public string[] Ipv6Servers { get; init; } = Array.Empty<string>();

    public string Describe() => Servers.Length == 0
        ? "自动获取（DHCP）"
        : string.Join(" , ", Servers);

    public string DescribeIpv6() => Ipv6Servers.Length == 0
        ? "自动获取（DHCP）"
        : string.Join(" , ", Ipv6Servers);
}

/// <summary>
/// 读写网卡 DNS：探测活动网卡、读取当前配置、一键设置主/备 DNS、还原之前的配置。
/// 通过 netsh 用接口索引操作，避免网卡名含空格带来的引号问题。
/// </summary>
public static class DnsConfigurator
{
    private sealed class DnsBackup
    {
        public int Index { get; set; } = -1;
        public string AdapterId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool WasDhcp { get; set; }
        public string[] Servers { get; set; } = Array.Empty<string>();
        public bool WasIpv6Dhcp { get; set; }
        public string[] Ipv6Servers { get; set; } = Array.Empty<string>();
        public DateTime At { get; set; }
    }

    public static string BackupPath => BackupStore.DnsBackupPath;

    /// <summary>最近一次 netsh 失败的错误摘要（供界面展示）。</summary>
    public static string LastNetshError { get; private set; } = "";

    // ------------------------------------------------------------ 探测

    /// <summary>枚举所有可设置 DNS 的活动网卡，优先返回带 IPv4 默认网关的。</summary>
    public static IReadOnlyList<AdapterDnsInfo> DetectAll()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            if (adapters.Count == 0) return Array.Empty<AdapterDnsInfo>();

            var withGateway = adapters.Where(a => HasGateway(a, AddressFamily.InterNetwork)).ToList();
            var ordered = withGateway
                .Concat(adapters.Where(a => !withGateway.Contains(a)))
                .ToList();

            return ordered
                .Select(BuildInfo)
                .Where(info => info is not null && info.Index >= 0)
                .Select(info => info!)
                .ToList();
        }
        catch
        {
            return Array.Empty<AdapterDnsInfo>();
        }
    }

    /// <summary>找出当前上网用的网卡（优先按 AdapterId 命中，其次选带 IPv4 网关的第一块）。</summary>
    public static AdapterDnsInfo? DetectActive(string? adapterId = null)
    {
        var all = DetectAll();
        if (all.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            var match = all.FirstOrDefault(a => string.Equals(a.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return all[0];
    }

    private static AdapterDnsInfo? BuildInfo(NetworkInterface adapter)
    {
        try
        {
            var index = -1;
            try
            {
                index = adapter.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
            }
            catch
            {
                // 少数虚拟网卡取不到 IPv4 属性
            }

            var statics = ReadServers(adapter.Id, "NameServer", AddressFamily.InterNetwork);
            var dhcp = ReadServers(adapter.Id, "DhcpNameServer", AddressFamily.InterNetwork);
            var servers = statics.Length > 0 ? statics : dhcp;

            var v6Statics = ReadServers(adapter.Id, "NameServer", AddressFamily.InterNetworkV6);
            var v6Dhcp = ReadServers(adapter.Id, "DhcpNameServer", AddressFamily.InterNetworkV6);
            var v6Servers = v6Statics.Length > 0 ? v6Statics : v6Dhcp;

            return new AdapterDnsInfo
            {
                Name = adapter.Name,
                Index = index,
                AdapterId = adapter.Id,
                IsDhcp = statics.Length == 0,
                Servers = servers,
                IsIpv6Dhcp = v6Statics.Length == 0,
                Ipv6Servers = v6Servers
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGateway(NetworkInterface adapter, AddressFamily family)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == family);
        }
        catch
        {
            return false;
        }
    }

    private static string[] ReadServers(string adapterId, string valueName, AddressFamily family)
    {
        try
        {
            var basePath = family == AddressFamily.InterNetwork
                ? @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"
                : @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

            using var key = Registry.LocalMachine.OpenSubKey($@"{basePath}\{adapterId}");
            var raw = key?.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

            return raw
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => IPAddress.TryParse(s, out var a) && a.AddressFamily == family)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------ 设置

    /// <summary>
    /// 把指定网卡的 DNS 一次性设为主/备。
    /// IPv6 仅在 clearIpv6 为 true 或提供了 ipv6Primary 时才改动，否则完全保持原样。
    /// 任何一步失败都会尝试回滚到修改前的 IPv4 + IPv6 配置。
    /// </summary>
    public static string Apply(
        AdapterDnsInfo adapter,
        string primary,
        string secondary,
        string ipv6Primary = "",
        string ipv6Secondary = "",
        bool clearIpv6 = false)
    {
        if (adapter is null || adapter.Index < 0) throw new InvalidOperationException("未找到有效的网卡索引");

        primary = (primary ?? "").Trim();
        secondary = (secondary ?? "").Trim();
        ipv6Primary = (ipv6Primary ?? "").Trim();
        ipv6Secondary = (ipv6Secondary ?? "").Trim();

        if (!IsIpv4(primary)) throw new InvalidOperationException("主 DNS 地址无效");
        if (secondary.Length > 0 && !IsIpv4(secondary))
            throw new InvalidOperationException("备选 DNS 地址无效");
        if (ipv6Primary.Length > 0 && !IsIpv6(ipv6Primary))
            throw new InvalidOperationException("IPv6 主 DNS 地址无效");
        if (ipv6Secondary.Length > 0 && !IsIpv6(ipv6Secondary))
            throw new InvalidOperationException("IPv6 备选 DNS 地址无效");

        BackupStore.Ensure();
        SaveBackup(adapter);

        var step = "设置 IPv4 主 DNS";
        if (!SetIpv4(adapter.Index, primary, secondary))
            throw RollbackAndFail(adapter, step, LastNetshError);

        var ipv6Part = "IPv6 DNS 未改动";
        if (clearIpv6 || ipv6Primary.Length > 0)
        {
            step = ipv6Primary.Length > 0 ? "设置 IPv6 DNS" : "清空 IPv6 DNS";
            if (!SetIpv6(adapter.Index, ipv6Primary, ipv6Secondary, clearIpv6 && ipv6Primary.Length == 0))
                throw RollbackAndFail(adapter, step, LastNetshError);

            ipv6Part = ipv6Primary.Length > 0
                ? $"IPv6 主 {ipv6Primary}" + (ipv6Secondary.Length > 0 ? $" · 备 {ipv6Secondary}" : "")
                : "IPv6 DNS 已清空为自动获取";
        }

        HostsManager.FlushDns();

        var v4Part = $"IPv4 主 {primary}" + (secondary.Length > 0 ? $" · 备 {secondary}" : "");
        return $"{v4Part}；{ipv6Part}";
    }

    private static InvalidOperationException RollbackAndFail(AdapterDnsInfo snapshot, string step, string reason)
    {
        var failures = new List<string>();
        RestoreSnapshot(snapshot, failures);

        var detail = failures.Count == 0
            ? "已回滚到修改前的配置"
            : "自动回滚未完全成功（" + string.Join("；", failures) + "），请手动检查";

        return new InvalidOperationException($"{step}失败：{reason}；{detail}");
    }

    private static bool SetIpv4(int index, string primary, string secondary)
    {
        if (!RunNetsh("interface", "ipv4", "set", "dnsservers",
                $"name={index}", "source=static", $"address={primary}", "register=primary"))
            return false;

        if (secondary.Length == 0) return true;

        if (!RunNetsh("interface", "ipv4", "add", "dnsservers",
                $"name={index}", $"address={secondary}", "index=2"))
        {
            LastNetshError = "备选 DNS 写入失败：" + LastNetshError;
            return false;
        }
        return true;
    }

    private static bool SetIpv6(int index, string primary, string secondary, bool clear)
    {
        if (clear)
            return RunNetsh("interface", "ipv6", "set", "dnsservers", $"name={index}", "source=dhcp");

        if (!RunNetsh("interface", "ipv6", "set", "dnsservers",
                $"name={index}", "source=static", $"address={primary}", "register=primary"))
            return false;

        if (secondary.Length == 0) return true;

        return RunNetsh("interface", "ipv6", "add", "dnsservers",
            $"name={index}", $"address={secondary}", "index=2");
    }

    private static bool IsIpv4(string value) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsIpv6(string value) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetworkV6;

    // ------------------------------------------------------------ 还原

    /// <summary>还原上一次保存的 DNS 配置；没有备份时退回自动获取。逐条校验并如实报告失败项。</summary>
    public static string Restore()
    {
        BackupStore.Ensure();

        var backup = LoadBackup();
        if (backup is null)
        {
            var adapter = DetectAll().FirstOrDefault();
            if (adapter is null) throw new InvalidOperationException("未检测到活动网卡");

            var failures = new List<string>();
            Check(failures, "IPv4 恢复自动获取", ApplyIpv4Snapshot(adapter.Index, true, Array.Empty<string>()));
            Check(failures, "IPv6 恢复自动获取", ApplyIpv6Snapshot(adapter.Index, true, Array.Empty<string>()));
            HostsManager.FlushDns();

            return failures.Count == 0
                ? "未找到 DNS 备份，已把 DNS 恢复为自动获取（DHCP）"
                : "恢复为自动获取未完全成功：" + string.Join("；", failures);
        }

        var target = DetectActive(backup.AdapterId)
                     ?? DetectAll().FirstOrDefault(a => a.Index == backup.Index);
        if (target is null) throw new InvalidOperationException("找不到备份对应的网卡，无法还原");

        var fails = new List<string>();
        Check(fails, "IPv4", ApplyIpv4Snapshot(target.Index, backup.WasDhcp, backup.Servers));
        Check(fails, "IPv6", ApplyIpv6Snapshot(target.Index, backup.WasIpv6Dhcp, backup.Ipv6Servers));
        HostsManager.FlushDns();

        if (fails.Count > 0)
            return $"「{target.Name}」还原未完全成功：{string.Join("；", fails)}";

        var summary = backup.WasDhcp || backup.Servers.Length == 0
            ? "自动获取（DHCP）"
            : string.Join(" , ", backup.Servers);
        var v6Summary = backup.Ipv6Servers.Length > 0 ? $"；IPv6 {string.Join(" , ", backup.Ipv6Servers)}" : "";
        return $"已把「{target.Name}」还原为：{summary}{v6Summary}";
    }

    private static void Check(List<string> failures, string label, bool ok)
    {
        if (!ok) failures.Add($"{label} 失败（{LastNetshError}）");
    }

    private static bool ApplyIpv4Snapshot(int index, bool wasDhcp, string[] servers)
    {
        if (wasDhcp || servers.Length == 0)
            return RunNetsh("interface", "ipv4", "set", "dnsservers", $"name={index}", "source=dhcp");

        if (!RunNetsh("interface", "ipv4", "set", "dnsservers",
                $"name={index}", "source=static", $"address={servers[0]}", "register=primary"))
            return false;

        for (var i = 1; i < servers.Length && i < 3; i++)
        {
            if (!RunNetsh("interface", "ipv4", "add", "dnsservers",
                    $"name={index}", $"address={servers[i]}", $"index={i + 1}"))
                return false;
        }
        return true;
    }

    private static bool ApplyIpv6Snapshot(int index, bool wasDhcp, string[] servers)
    {
        if (wasDhcp || servers.Length == 0)
            return RunNetsh("interface", "ipv6", "set", "dnsservers", $"name={index}", "source=dhcp");

        if (!RunNetsh("interface", "ipv6", "set", "dnsservers",
                $"name={index}", "source=static", $"address={servers[0]}", "register=primary"))
            return false;

        for (var i = 1; i < servers.Length && i < 3; i++)
        {
            if (!RunNetsh("interface", "ipv6", "add", "dnsservers",
                    $"name={index}", $"address={servers[i]}", $"index={i + 1}"))
                return false;
        }
        return true;
    }

    /// <summary>把网卡恢复成给定快照（IPv4 + IPv6），返回失败项列表（空表示全部成功）。</summary>
    public static List<string> RestoreSnapshot(AdapterDnsInfo snapshot)
    {
        var failures = new List<string>();
        RestoreSnapshot(snapshot, failures);
        HostsManager.FlushDns();
        return failures;
    }

    private static void RestoreSnapshot(AdapterDnsInfo snapshot, List<string> failures)
    {
        Check(failures, "IPv4", ApplyIpv4Snapshot(snapshot.Index, snapshot.IsDhcp, snapshot.Servers));
        Check(failures, "IPv6", ApplyIpv6Snapshot(snapshot.Index, snapshot.IsIpv6Dhcp, snapshot.Ipv6Servers));
    }

    public static bool HasBackup() => File.Exists(BackupStore.DnsBackupPath);

    private static void SaveBackup(AdapterDnsInfo adapter)
    {
        if (File.Exists(BackupStore.DnsBackupPath)) return; // 只保留最初一次，避免把「优化后」的状态覆盖原始配置

        var backup = new DnsBackup
        {
            Index = adapter.Index,
            AdapterId = adapter.AdapterId,
            Name = adapter.Name,
            WasDhcp = adapter.IsDhcp,
            Servers = adapter.Servers,
            WasIpv6Dhcp = adapter.IsIpv6Dhcp,
            Ipv6Servers = adapter.Ipv6Servers,
            At = DateTime.Now
        };

        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(BackupStore.Root);
        File.WriteAllText(BackupStore.DnsBackupPath, json);
        BackupStore.HardenFile(BackupStore.DnsBackupPath);
    }

    private static DnsBackup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupStore.DnsBackupPath)) return null;
            return JsonSerializer.Deserialize<DnsBackup>(File.ReadAllText(BackupStore.DnsBackupPath));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ netsh

    private static bool RunNetsh(params string[] arguments)
    {
        var error = RunNetshDetailed(arguments);
        LastNetshError = error ?? "";
        return error is null;
    }

    /// <summary>执行 netsh，成功返回 null，失败返回错误摘要（优先使用 stderr）。</summary>
    private static string? RunNetshDetailed(string[] arguments)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo("netsh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            process = Process.Start(startInfo);
            if (process is null) return "无法启动 netsh";

            // 先异步读完两个流，避免管道缓冲区写满导致进程卡死
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return "netsh 执行超时（15 秒）";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();

            if (process.ExitCode == 0) return null;
            if (stderr.Length > 0) return Truncate(stderr);
            if (stdout.Length > 0) return Truncate(stdout);
            return $"netsh 退出码 {process.ExitCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}
