using System.ComponentModel;

namespace NetBoost.Models;

/// <summary>一个可选的公共 DNS 服务（主/备地址对）。</summary>
public sealed class DnsServer : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Primary { get; init; }
    public string Secondary { get; init; } = "";
    public string Note { get; init; } = "";

    /// <summary>主/备成对显示，供快捷设置卡片里一行看完。</summary>
    public string PairText => Secondary.Length == 0 ? Primary : $"{Primary} / {Secondary}";

    private bool _isCurrent;

    /// <summary>该组合是否与网卡当前生效的主/备完全一致，用于快捷卡片高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    private bool _pending;

    /// <summary>卡片已点一次、等待第二次点击确认（避免每点都弹 MessageBox）。</summary>
    public bool Pending
    {
        get => _pending;
        set
        {
            if (_pending == value) return;
            _pending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pending)));
        }
    }

    private string _measuredText = "";

    /// <summary>最近一次 DNS 测速结果，回填到卡片副标题。</summary>
    public string MeasuredText
    {
        get => _measuredText;
        set
        {
            if (_measuredText == value) return;
            _measuredText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MeasuredText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 内置常用公共 DNS。这里不联网拉取列表：离线可用、结果可复现，
/// 而且国内能连通的公共 DNS 本来就这么几家。
/// 每条都带主+备成对地址，供 DNS 页「快捷设置」一次性写入。
/// </summary>
public static class DnsServerCatalog
{
    public static IReadOnlyList<DnsServer> Candidates { get; } = new List<DnsServer>
    {
        new()
        {
            Name = "阿里云 DNS",
            Primary = "223.5.5.5",
            Secondary = "223.6.6.6",
            Note = "国内节点多，解析快"
        },
        new()
        {
            Name = "腾讯 DNSPod",
            Primary = "119.29.29.29",
            Secondary = "182.254.116.116",
            Note = "国内主流，抗污染较好"
        },
        new()
        {
            Name = "114DNS",
            Primary = "114.114.114.114",
            Secondary = "114.114.115.115",
            Note = "老牌公共 DNS，覆盖广"
        },
        new()
        {
            Name = "百度 DNS",
            Primary = "180.76.76.76",
            Secondary = "",
            Note = "国内单点地址，无备选"
        },
        new()
        {
            Name = "Cloudflare",
            Primary = "1.1.1.1",
            Secondary = "1.0.0.1",
            Note = "海外，国内可能不稳定"
        },
        new()
        {
            Name = "Quad9",
            Primary = "9.9.9.9",
            Secondary = "9.9.9.10",
            Note = "海外，带恶意域名拦截"
        },
        new()
        {
            Name = "Google DNS",
            Primary = "8.8.8.8",
            Secondary = "8.8.4.4",
            Note = "海外，国内通常不可用"
        },
        new()
        {
            Name = "OpenDNS",
            Primary = "208.67.222.222",
            Secondary = "208.67.220.220",
            Note = "海外，国内通常不可用"
        }
    };
}
