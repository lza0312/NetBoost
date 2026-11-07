using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NetBoost.Core;

/// <summary>提权子进程返回的结果。</summary>
public sealed class ElevatedResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public int Written { get; set; }
    public int RemovedConflicts { get; set; }
}

/// <summary>
/// 写入 hosts / 修改 DNS 需要管理员权限。程序本身以普通权限运行（asInvoker），
/// 真正写入时通过 runas 启动一个同程序的提权实例完成操作，操作结果通过临时 JSON 返回。
/// </summary>
public static class Elevation
{
    public const string ActionSwitch = "--netboost-action";
    private const string HostsApply = "hosts-apply";
    private const string HostsRestore = "hosts-restore";
    private const string DnsApply = "dns-apply";
    private const string DnsRestore = "dns-restore";

    public static bool IsElevated => HostsManager.IsElevated;

    // ------------------------------------------------------------ 对外操作入口

    public static ApplyOutcome ApplyHosts(IReadOnlyDictionary<string, string> maps)
    {
        if (IsElevated) return HostsManager.Apply(maps);

        var response = Run(HostsApply, new HostsApplyPayload { Maps = maps });
        if (!response.Ok) throw new InvalidOperationException(response.Message);
        return new ApplyOutcome
        {
            BackupPath = response.BackupPath,
            Written = response.Written,
            RemovedConflicts = response.RemovedConflicts
        };
    }

    /// <summary>把 hosts 还原为指定备份；backupFileName 为空则仅移除 NetBoost 托管块。</summary>
    public static string RestoreHosts(string? backupFileName)
    {
        if (IsElevated)
            return RestoreHostsCore(backupFileName);

        var response = Run(HostsRestore, new HostsRestorePayload { BackupFileName = backupFileName ?? "" });
        if (!response.Ok) throw new InvalidOperationException(response.Message);
        return response.Message;
    }

    public static string ApplyDns(
        AdapterDnsInfo adapter,
        string primary,
        string secondary,
        string ipv6Primary,
        string ipv6Secondary,
        bool handleIpv6)
    {
        if (IsElevated)
            return DnsConfigurator.Apply(adapter, primary, secondary, ipv6Primary, ipv6Secondary, handleIpv6);

        var payload = new DnsApplyPayload
        {
            AdapterId = adapter.AdapterId,
            Index = adapter.Index,
            Primary = primary,
            Secondary = secondary,
            Ipv6Primary = ipv6Primary,
            Ipv6Secondary = ipv6Secondary,
            HandleIpv6 = handleIpv6
        };
        var response = Run(DnsApply, payload);
        if (!response.Ok) throw new InvalidOperationException(response.Message);
        return response.Message;
    }

    public static string RestoreDns()
    {
        if (IsElevated) return DnsConfigurator.Restore();
        var response = Run(DnsRestore, new DnsRestorePayload());
        if (!response.Ok) throw new InvalidOperationException(response.Message);
        return response.Message;
    }

    // ------------------------------------------------------------ 提权子进程

    /// <summary>以管理员身份启动自身并等待结果。用户取消 UAC 时返回 Ok=false。</summary>
    private static ElevatedResponse Run(string action, object payload)
    {
        var id = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(Path.GetTempPath(), $"netboost-{id}.in.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"netboost-{id}.out.json");

        try
        {
            File.WriteAllText(inputPath, JsonSerializer.Serialize(payload));

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return new ElevatedResponse { Message = "无法定位程序自身路径" };

            var arguments = $"{ActionSwitch} {action} \"{inputPath}\" \"{outputPath}\"";
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var process = Process.Start(startInfo);
            if (process is null)
                return new ElevatedResponse { Message = "无法启动管理员进程" };

            process.WaitForExit(60000);

            if (!File.Exists(outputPath))
                return new ElevatedResponse { Message = "提权操作未返回结果（可能被安全软件拦截）" };

            return JsonSerializer.Deserialize<ElevatedResponse>(File.ReadAllText(outputPath))
                   ?? new ElevatedResponse { Message = "提权结果解析失败" };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedResponse { Message = "已取消管理员授权（UAC），操作未执行" };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Message = ex.Message };
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>在提权实例中处理动作；返回 true 表示本次进程只执行动作，不应打开主界面。</summary>
    public static bool TryRunFromCommandLine(string[] args)
    {
        var index = Array.IndexOf(args, ActionSwitch);
        if (index < 0 || index + 3 >= args.Length) return false;

        var action = args[index + 1];
        var inputPath = args[index + 2];
        var outputPath = args[index + 3];

        ElevatedResponse response;
        try
        {
            var payload = File.ReadAllText(inputPath);
            response = Execute(action, payload);
        }
        catch (Exception ex)
        {
            response = new ElevatedResponse { Message = ex.Message };
        }

        try
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(response));
        }
        catch
        {
            // 无法写回结果时只能静默失败
        }

        return true;
    }

    private static ElevatedResponse Execute(string action, string payloadJson)
    {
        switch (action)
        {
            case HostsApply:
            {
                var payload = JsonSerializer.Deserialize<HostsApplyPayload>(payloadJson)
                              ?? throw new InvalidOperationException("参数无效");
                var outcome = HostsManager.Apply(payload.Maps);
                HostsManager.FlushDns();
                return new ElevatedResponse
                {
                    Ok = true,
                    BackupPath = outcome.BackupPath,
                    Written = outcome.Written,
                    RemovedConflicts = outcome.RemovedConflicts
                };
            }

            case HostsRestore:
            {
                var payload = JsonSerializer.Deserialize<HostsRestorePayload>(payloadJson);
                var message = RestoreHostsCore(payload?.BackupFileName ?? "");
                return new ElevatedResponse { Ok = true, Message = message };
            }

            case DnsApply:
            {
                var payload = JsonSerializer.Deserialize<DnsApplyPayload>(payloadJson)
                              ?? throw new InvalidOperationException("参数无效");

                var adapter = DnsConfigurator.DetectActive(payload.AdapterId);
                if (adapter is null)
                    throw new InvalidOperationException("未找到要修改的网卡（可能已断开）");

                var message = DnsConfigurator.Apply(
                    adapter, payload.Primary, payload.Secondary,
                    payload.Ipv6Primary, payload.Ipv6Secondary, payload.HandleIpv6);

                return new ElevatedResponse { Ok = true, Message = message };
            }

            case DnsRestore:
            {
                var message = DnsConfigurator.Restore();
                return new ElevatedResponse { Ok = true, Message = message };
            }

            default:
                return new ElevatedResponse { Message = $"未知操作：{action}" };
        }
    }

    private static string RestoreHostsCore(string backupFileName)
    {
        var backupPath = BackupRoot(backupFileName);
        if (backupPath is not null)
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            File.WriteAllText(HostsManager.HostsPath,
                File.ReadAllText(backupPath, System.Text.Encoding.UTF8), utf8);
            HostsManager.FlushDns();
            return $"已还原自备份：{Path.GetFileName(backupPath)}";
        }

        HostsManager.RemoveManagedBlock();
        HostsManager.FlushDns();
        return "已移除 NetBoost 托管块";
    }

    /// <summary>按文件名定位 hosts 备份，避免提权进程接受任意路径。</summary>
    private static string? BackupRoot(string backupFileName)
    {
        if (string.IsNullOrWhiteSpace(backupFileName)) return null;
        var name = Path.GetFileName(backupFileName);
        if (!name.StartsWith("hosts_", StringComparison.OrdinalIgnoreCase)) return null;

        var candidate = Path.Combine(BackupStore.HostsDir, name);
        return File.Exists(candidate) ? candidate : null;
    }

    // ------------------------------------------------------------ 参数模型

    private sealed class HostsApplyPayload
    {
        public IReadOnlyDictionary<string, string> Maps { get; set; } = new Dictionary<string, string>();
    }

    private sealed class HostsRestorePayload
    {
        public string BackupFileName { get; set; } = "";
    }

    private sealed class DnsApplyPayload
    {
        public string AdapterId { get; set; } = "";
        public int Index { get; set; } = -1;
        public string Primary { get; set; } = "";
        public string Secondary { get; set; } = "";
        public string Ipv6Primary { get; set; } = "";
        public string Ipv6Secondary { get; set; } = "";
        public bool HandleIpv6 { get; set; }
    }

    private sealed class DnsRestorePayload
    {
    }
}
