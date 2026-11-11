using System.Text.Json.Serialization;

namespace SimpleVpn.Core.Models;

/// <summary>How traffic is captured.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionMode>))]
public enum ConnectionMode
{
    /// <summary>Windows system proxy (no admin rights).</summary>
    [JsonStringEnumMemberName("proxy")]
    SystemProxy,

    /// <summary>TUN adapter, all system traffic (admin rights). Not implemented yet (M4).</summary>
    [JsonStringEnumMemberName("tun")]
    Tun,
}

public sealed record DnsSettings
{
    public const string DefaultRemoteServer = "https://1.1.1.1/dns-query";

    /// <summary>"localhost" means the system resolver (bypasses Xray routing, always direct).</summary>
    public const string SystemResolver = "localhost";

    /// <summary>Resolver for proxied domains; queries go through the proxy. Any Xray DNS address (DoH URL, IP).</summary>
    public string RemoteServer { get; set; } = DefaultRemoteServer;

    /// <summary>Resolver for direct domains: <see cref="SystemResolver"/> or a plain IP address (UDP).</summary>
    public string DirectServer { get; set; } = SystemResolver;

    /// <summary>Resolve only IPv4 addresses (many home connections have no IPv6).</summary>
    public bool PreferIPv4 { get; set; } = true;
}

/// <summary>Where geoip.dat / geosite.dat come from and how often they are refreshed.</summary>
public sealed record GeoSettings
{
    public const string DefaultBaseUrl = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/";

    public static readonly IReadOnlyList<int> IntervalChoicesHours = [6, 12, 24, 72, 168];

    public bool AutoUpdate { get; set; } = true;

    public int IntervalHours { get; set; } = 24;

    /// <summary>False = runetfreedom (default); true = <see cref="CustomGeoipUrl"/> / <see cref="CustomGeositeUrl"/>.</summary>
    public bool UseCustomSource { get; set; }

    public string? CustomGeoipUrl { get; set; }

    public string? CustomGeositeUrl { get; set; }
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public static readonly IReadOnlyList<string> LogLevels = ["debug", "info", "warning", "error", "none"];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>UI language ("ru" / "en"); null = follow the OS.</summary>
    public string? Language { get; set; }

    public ConnectionMode Mode { get; set; } = ConnectionMode.SystemProxy;

    /// <summary>Active routing preset id; unknown ids fall back to the default built-in preset.</summary>
    public string? SelectedPresetId { get; set; }

    public DnsSettings Dns { get; set; } = new();

    public GeoSettings Geo { get; set; } = new();

    /// <summary>Preferred local SOCKS port. A free port is chosen automatically when it is busy.</summary>
    public int SocksPort { get; set; } = 10888;

    /// <summary>Preferred local HTTP port (used for the Windows system proxy).</summary>
    public int HttpPort { get; set; } = 10889;

    /// <summary>Xray log level (core output goes to our log file).</summary>
    public string CoreLogLevel { get; set; } = "warning";
}
