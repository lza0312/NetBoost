using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NetBoost.Core;

/// <summary>
/// 探测结果内存缓存：同一网络指纹下，5 分钟内重复检测直接复用结果，避免每次全量重测。
/// 网络指纹由活动网卡的 Id 与 IPv4 地址组成，切换 Wi-Fi / 插拔网线后缓存自动失效。
/// </summary>
public static class ResultCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, (DomainResult Result, DateTime At)> Map =
        new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    private static string _signature = "";
    private static DateTime _signatureAt = DateTime.MinValue;

    public static string Signature()
    {
        if (_signature.Length > 0 && (DateTime.UtcNow - _signatureAt).TotalSeconds < 5)
            return _signature;

        var builder = new StringBuilder();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                builder.Append(adapter.Id).Append(':');
                try
                {
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        builder.Append(unicast.Address).Append(',');
                    }
                }
                catch
                {
                    // 个别网卡读不到 IP 属性
                }
                builder.Append(';');
            }
        }
        catch
        {
            builder.Append("unknown");
        }

        _signature = builder.ToString();
        _signatureAt = DateTime.UtcNow;
        return _signature;
    }

    public static DomainResult? TryGet(string signature, string host)
    {
        var key = signature + "|" + host.ToLowerInvariant();
        lock (Sync)
        {
            if (!Map.TryGetValue(key, out var entry)) return null;
            if (DateTime.UtcNow - entry.At > Ttl)
            {
                Map.Remove(key);
                return null;
            }
            return entry.Result.Clone();
        }
    }

    public static void Set(string signature, string host, DomainResult result)
    {
        var key = signature + "|" + host.ToLowerInvariant();
        lock (Sync) Map[key] = (result.Clone(), DateTime.UtcNow);
    }

    public static void Clear()
    {
        lock (Sync) Map.Clear();
    }
}
