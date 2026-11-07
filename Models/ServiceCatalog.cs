namespace NetBoost.Models;

/// <summary>
/// 内置服务与域名清单。域名都是这些平台实际用于网页 / 客户端 / CDN 的主机名，
/// hosts 只能精确匹配，所以这里不做通配符。
/// </summary>
public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceDefinition> All { get; } = new List<ServiceDefinition>
    {
        new()
        {
            Id = "github",
            Name = "GitHub",
            Description = "代码托管 · Release · Raw 文件下载",
            Accent = "#8B9CFF",
            ProbeUrl = "https://github.com/favicon.ico",
            Domains = new[]
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
            }
        },
        new()
        {
            Id = "twitch",
            Name = "Twitch",
            Description = "直播 · 弹幕 · 静态资源",
            Accent = "#9B7BF7",
            ProbeUrl = "https://static.twitch.tv/assets/favicon-32.png",
            Domains = new[]
            {
                "twitch.tv",
                "www.twitch.tv",
                "dashboard.twitch.tv",
                "gql.twitch.tv",
                "static.twitch.tv",
                "clips.twitch.tv",
                "passport.twitch.tv",
                "spade.twitch.tv"
            }
        },
        new()
        {
            Id = "steam",
            Name = "Steam",
            Description = "商店 · 社区 · 内容分发",
            Accent = "#5AA9E6",
            ProbeUrl = "https://store.steampowered.com/favicon.ico",
            Domains = new[]
            {
                "store.steampowered.com",
                "steamcommunity.com",
                "www.steamcommunity.com",
                "api.steampowered.com",
                "help.steampowered.com",
                "checkout.steampowered.com",
                "steamcdn-a.akamaihd.net",
                "cdn.steamstatic.com",
                "cdn.cloudflare.steamstatic.com",
                "media.steampowered.com"
            }
        },
        new()
        {
            Id = "gog",
            Name = "GOG Galaxy",
            Description = "GOG 商店 · Galaxy 客户端",
            Accent = "#B06AE8",
            ProbeUrl = "https://www.gog.com/favicon.ico",
            Domains = new[]
            {
                "gog.com",
                "www.gog.com",
                "api.gog.com",
                "content-system.gog.com",
                "cdn.gog.com",
                "galaxy.gog.com",
                "support.gog.com"
            }
        },
        new()
        {
            Id = "ubisoft",
            Name = "Ubisoft Connect",
            Description = "Uplay · 育碧商店 · 静态资源",
            Accent = "#00A8E8",
            ProbeUrl = "https://ubisoft.com/",
            Domains = new[]
            {
                "ubisoft.com",
                "www.ubisoft.com",
                "ubisoftconnect.com",
                "connect.ubisoft.com",
                "store.ubi.com",
                "static2.cdn.ubi.com",
                "static3.cdn.ubi.com",
                "ubistatic-a.akamaihd.net",
                "ubistatic19-a.akamaihd.net"
            }
        },
        new()
        {
            Id = "discord",
            Name = "Discord",
            Description = "聊天 · 语音 · CDN 附件",
            Accent = "#6E7BF2",
            ProbeUrl = "https://discord.com/assets/favicon.ico",
            Domains = new[]
            {
                "discord.com",
                "discord.gg",
                "cdn.discordapp.com",
                "media.discordapp.net",
                "gateway.discord.gg"
            }
        },
        new()
        {
            Id = "battle",
            Name = "Battle.net",
            Description = "暴雪客户端 · 游戏下载",
            Accent = "#3FA9F5",
            ProbeUrl = "https://www.battle.net/",
            Domains = new[]
            {
                "battle.net",
                "www.battle.net",
                "us.battle.net",
                "eu.battle.net",
                "account.battle.net",
                "cn.battle.net"
            }
        },
        new()
        {
            Id = "epic",
            Name = "Epic Games",
            Description = "Epic 商店 · 启动器",
            Accent = "#B7C0CF",
            ProbeUrl = "https://store.epicgames.com/",
            Domains = new[]
            {
                "epicgames.com",
                "www.epicgames.com",
                "store.epicgames.com",
                "launcher.epicgames.com",
                "accounts.epicgames.com"
            }
        }
    };
}
