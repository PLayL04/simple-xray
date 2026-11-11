using System.Globalization;
using System.Text.Json.Serialization;

namespace SimpleXray.Core.Routing;

/// <summary>Where matched traffic goes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RouteOutbound>))]
public enum RouteOutbound
{
    [JsonStringEnumMemberName("proxy")]
    Proxy,

    [JsonStringEnumMemberName("direct")]
    Direct,

    [JsonStringEnumMemberName("block")]
    Block,
}

/// <summary>
/// One routing rule: a subset of Xray rule fields plus the target.
/// Domains and IPs are combined with OR (the generator splits them into separate Xray rules);
/// port, network, protocol and process narrow the match (AND).
/// </summary>
public sealed record RoutingRule
{
    public bool Enabled { get; set; } = true;

    /// <summary>Optional comment shown in the editor.</summary>
    public string? Name { get; set; }

    /// <summary>Xray domain matchers: <c>geosite:</c>, <c>domain:</c>, <c>full:</c>, <c>keyword:</c>, <c>regexp:</c>, <c>ext:</c> or a plain substring.</summary>
    public IReadOnlyList<string> Domain { get; set; } = [];

    /// <summary>IP addresses, CIDR ranges, <c>geoip:</c> or <c>ext:</c>.</summary>
    public IReadOnlyList<string> Ip { get; set; } = [];

    /// <summary>Xray port list: <c>443</c>, <c>1000-2000</c>, <c>53,443,8000-8080</c>.</summary>
    public string? Port { get; set; }

    /// <summary><c>tcp</c>, <c>udp</c> or <c>tcp,udp</c>.</summary>
    public string? Network { get; set; }

    /// <summary>Sniffed protocols: <c>http</c>, <c>tls</c>, <c>quic</c>, <c>bittorrent</c>.</summary>
    public IReadOnlyList<string> Protocol { get; set; } = [];

    /// <summary>Process names for per-app routing. Reserved: not editable in the UI until per-app support.</summary>
    public IReadOnlyList<string> Process { get; set; } = [];

    public RouteOutbound Outbound { get; set; } = RouteOutbound.Proxy;

    [JsonIgnore]
    public bool HasMatcher =>
        Domain.Count > 0 || Ip.Count > 0 || !string.IsNullOrEmpty(Port) || !string.IsNullOrEmpty(Network) ||
        Protocol.Count > 0 || Process.Count > 0;
}

/// <summary>
/// A routing preset: ordered rules (first match wins) plus the outbound for unmatched traffic.
/// Built-in presets are embedded resources and immutable; user presets live in the presets folder.
/// </summary>
public sealed record RoutingPreset
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = string.Empty;

    /// <summary>Name used when <see cref="Names"/> has no entry for the UI language.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Localized names by two-letter language code (used by built-in presets).</summary>
    public IReadOnlyDictionary<string, string>? Names { get; set; }

    /// <summary>Xray <c>domainStrategy</c>: AsIs, IPIfNonMatch or IPOnDemand.</summary>
    public string DomainStrategy { get; set; } = "IPIfNonMatch";

    public IReadOnlyList<RoutingRule> Rules { get; set; } = [];

    public RouteOutbound DefaultOutbound { get; set; } = RouteOutbound.Proxy;

    /// <summary>URL the preset was imported from; it can be re-downloaded to update the rules.</summary>
    public string? SourceUrl { get; set; }

    [JsonIgnore]
    public bool BuiltIn { get; set; }

    public string DisplayName(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (Names is not null && Names.TryGetValue(culture.TwoLetterISOLanguageName, out var localized) && !string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }

        return Names?.Values.FirstOrDefault() ?? Id;
    }
}
