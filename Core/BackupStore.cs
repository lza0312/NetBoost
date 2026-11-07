using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetBoost.Core;

/// <summary>
/// 备份统一存放目录：%ProgramData%\NetBoost。
/// 目录与备份文件仅允许管理员写入，普通用户可以读取（用于界面展示备份列表）。
/// 首次运行时把旧版本放在 exe 同目录的备份迁移过来。
/// </summary>
public static class BackupStore
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetBoost");

    public static string HostsDir { get; } = Path.Combine(Root, "hosts-backups");
    public static string DnsBackupPath { get; } = Path.Combine(Root, "dns-backup.json");

    private static string LegacyHostsDir { get; } = Path.Combine(AppContext.BaseDirectory, "hosts-backups");
    private static string LegacyDnsBackup { get; } = Path.Combine(AppContext.BaseDirectory, "dns-backup.json");

    /// <summary>创建目录、收紧权限并迁移旧备份。需要管理员权限；失败时静默跳过。</summary>
    public static void Ensure()
    {
        try
        {
            Directory.CreateDirectory(HostsDir);
            HardenDirectory(HostsDir);
            HardenFile(DnsBackupPath);
            MigrateLegacy();
        }
        catch
        {
            // 非 NTFS、权限不足等情况不影响主流程
        }
    }

    private static void MigrateLegacy()
    {
        try
        {
            if (File.Exists(LegacyDnsBackup) && !File.Exists(DnsBackupPath))
            {
                File.Copy(LegacyDnsBackup, DnsBackupPath);
                HardenFile(DnsBackupPath);
            }

            if (!Directory.Exists(LegacyHostsDir)) return;

            foreach (var file in Directory.EnumerateFiles(LegacyHostsDir, "hosts_*.bak"))
            {
                var target = Path.Combine(HostsDir, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                HardenFile(target);
            }
        }
        catch
        {
            // 迁移失败不阻塞
        }
    }

    private static void HardenDirectory(string path)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
            // 忽略非 NTFS 卷
        }
    }

    public static void HardenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddRules(security, InheritanceFlags.None);
            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // 忽略
        }
    }

    private static void AddRules(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        var flags = inheritance;
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, flags, PropagationFlags.None, AccessControlType.Allow));
    }
}
