namespace NetBoost.Models;

/// <summary>
/// 一个可优化的服务（GitHub / Twitch / Steam ...），包含一组域名和一个用于测速的代表性 URL。
/// </summary>
public sealed class ServiceDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Accent { get; init; }
    public required string[] Domains { get; init; }
    public required string ProbeUrl { get; init; }

    /// <summary>是否参与本次优化，由界面上的复选框双向绑定。</summary>
    public bool IsEnabled { get; set; } = true;

    public int DomainCount => Domains.Length;
}
