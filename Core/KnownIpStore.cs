using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace NetBoost.Core;

/// <summary>一张「域名 → 已知可用 IP」表，含来源与更新时间。</summary>
public sealed class KnownIpTable
{
    public string Source { get; init; } = "内置表";
    public DateTime? UpdatedAt { get; init; }
    public IReadOnlyDictionary<string, string[]> Map { get; init; } = new Dictionary<string, string[]>();

    public bool IsBuiltIn => UpdatedAt is null;
}

/// <summary>
/// GitHub 相关域名的已知可用 IP 兜底表。
/// 只在 DNS 解析不可用、解析出的 IP 全部连不上、或全部过不了证书校验时才使用；
/// 拉不到在线更新就继续用程序内置的那一份。所有兜底 IP 仍会走正常的 SNI/Host + 证书校验，
/// 不会为了连通而放宽安全策略。
/// </summary>
public static class KnownIpStore
{
    /// <summary>兜底表覆盖的域名（与 ServiceCatalog 的 GitHub 组保持一致）。</summary>
    public static readonly string[] Domains =
    {
        "github.com",
        "api.github.com",
        "gist.github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "github.githubassets.com",
        "avatars.githubusercontent.com",
        "user-images.githubusercontent.com",
        "github.io"
    };

    // 内置应急表：GitHub 各类边缘节点 + GitHub Pages/Fastly 的常用地址。
    // 这些 IP 会随时间变化，所以在线刷新与「用前实测」都是必需的。
    private static readonly string[] GithubEdge =
    {
        "140.82.112.3", "140.82.113.3", "140.82.114.3", "140.82.116.3",
        "20.205.243.166", "20.27.177.113"
    };

    private static readonly string[] RawCdn =
    {
        "185.199.108.133", "185.199.109.133", "185.199.110.133", "185.199.111.133"
    };

    private static readonly string[] AssetsCdn =
    {
        "185.199.108.154", "185.199.109.154", "185.199.110.154", "185.199.111.154"
    };

    private static readonly string[] PagesCdn =
    {
        "185.199.108.153", "185.199.109.153", "185.199.110.153", "185.199.111.153"
    };

    private static readonly IReadOnlyDictionary<string, string[]> BuiltIn = new Dictionary<string, string[]>
    {
        ["github.com"] = GithubEdge,
        ["api.github.com"] = GithubEdge,
        ["gist.github.com"] = GithubEdge,
        ["codeload.github.com"] = GithubEdge,
        ["raw.githubusercontent.com"] = RawCdn,
        ["objects.githubusercontent.com"] = RawCdn,
        ["avatars.githubusercontent.com"] = RawCdn,
        ["user-images.githubusercontent.com"] = RawCdn,
        ["github.githubassets.com"] = AssetsCdn,
        ["github.io"] = PagesCdn
    };

    private static readonly (string Name, string Url)[] OnlineSources =
    {
        ("GitHub520 hosts（jsDelivr）", "https://cdn.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts"),
        ("GitHub520 hosts（raw.githubusercontent.com）", "https://raw.githubusercontent.com/521xueweihan/GitHub520/main/hosts")
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly object Sync = new();

    private static readonly HttpClient Http = CreateClient();
    private static KnownIpTable? _current;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetBoost", "github-ips.json");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(9) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetBoost/1.4 (+hosts-fallback)");
        return client;
    }

    public static KnownIpTable Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= LoadCache() ?? BuiltInTable();
            }
        }
    }

    /// <summary>取某个域名的兜底 IP；没有对应条目时返回空列表，调用方应视为「没有兜底」。</summary>
    public static IReadOnlyList<string> GetIps(string host)
    {
        var key = host.ToLowerInvariant();
        return Current.Map.TryGetValue(key, out var ips) ? ips : Array.Empty<string>();
    }

    /// <summary>给界面显示：来源 + 更新时间（内置表没有在线更新时间）。</summary>
    public static string Describe()
    {
        var table = Current;
        return table.IsBuiltIn
            ? "内置表（在线更新未成功，随程序版本提供）"
            : $"{table.Source} · 更新于 {table.UpdatedAt:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// 尝试在线刷新并写缓存。force=false 时 24 小时内不重复请求；失败返回 false，
    /// 调用方保持当前表（在线表 → 旧缓存 → 内置表）。
    /// </summary>
    public static async Task<bool> RefreshAsync(bool force)
    {
        var current = Current;
        if (!force && current.UpdatedAt.HasValue &&
            DateTime.Now - current.UpdatedAt.Value < CacheTtl)
            return false;

        foreach (var source in OnlineSources)
        {
            try
            {
                var text = await Http.GetStringAsync(source.Url).ConfigureAwait(false);
                var map = ParseHosts(text);
                if (map.Count < 3) continue;

                var table = new KnownIpTable
                {
                    Source = source.Name,
                    UpdatedAt = DateTime.Now,
                    Map = Fill(map)
                };

                SaveCache(table);
                lock (Sync) _current = table;
                return true;
            }
            catch
            {
                // 单个来源失败就换下一个；全部失败保留当前表
            }
        }

        return false;
    }

    private static KnownIpTable BuiltInTable() => new()
    {
        Source = "内置表",
        UpdatedAt = null,
        Map = BuiltIn
    };

    private static KnownIpTable? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;

            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<CacheFile>(json);
            if (cache?.Map is null || cache.Map.Count < 3) return null;

            var filled = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var domain in Domains)
                filled[domain] = cache.Map.TryGetValue(domain, out var ips) && ips.Length > 0
                    ? Merge(ips, BuiltIn[domain])
                    : BuiltIn[domain];

            return new KnownIpTable
            {
                Source = cache.Source,
                UpdatedAt = cache.UpdatedAt,
                Map = filled
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(KnownIpTable table)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var cache = new CacheFile
            {
                Source = table.Source,
                UpdatedAt = table.UpdatedAt,
                Map = table.Map.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // 缓存写不进去不影响本次使用
        }
    }

    /// <summary>
    /// 在线源每个域名通常只给 1 个 IP，一旦这个 IP 失效兜底就形同虚设；
    /// 因此把内置表里其余的 IP 追加在后面，作为同一域名的备用节点。
    /// </summary>
    private static Dictionary<string, string[]> Fill(Dictionary<string, string[]> map)
    {
        var filled = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in Domains)
            filled[domain] = map.TryGetValue(domain, out var ips) && ips.Length > 0
                ? Merge(ips, BuiltIn[domain])
                : BuiltIn[domain];
        return filled;
    }

    private static string[] Merge(string[] primary, string[] extra)
    {
        var list = primary.ToList();
        foreach (var ip in extra)
            if (!list.Contains(ip, StringComparer.Ordinal) && list.Count < 6) list.Add(ip);
        return list.ToArray();
    }

    /// <summary>解析 hosts 格式文本，只保留兜底表覆盖的域名，每域名最多 6 个 IP。</summary>
    private static Dictionary<string, string[]> ParseHosts(string text)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var hosts = new HashSet<string>(Domains, StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (!IPAddress.TryParse(parts[0], out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                continue;

            var ip = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var host = parts[i].Trim().ToLowerInvariant();
                if (!hosts.Contains(host)) continue;

                var list = map.TryGetValue(host, out var existing) ? existing.ToList() : new List<string>();
                if (!list.Contains(ip, StringComparer.Ordinal) && list.Count < 6) list.Add(ip);
                map[host] = list.ToArray();
            }
        }

        return map;
    }

    private sealed class CacheFile
    {
        public string Source { get; set; } = "内置表";
        public DateTime? UpdatedAt { get; set; }
        public Dictionary<string, string[]> Map { get; set; } = new();
    }
}
