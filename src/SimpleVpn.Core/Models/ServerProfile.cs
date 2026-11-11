using System.Text.Json.Serialization;

namespace SimpleVpn.Core.Models;

/// <summary>
/// One proxy server imported from a share link. Field names follow the vless:// link
/// parameters (see <see cref="Parsing.VlessLinkParser"/>), values are kept as given
/// so the profile can be exported back to an equivalent link.
/// </summary>
public sealed record ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name (link fragment after '#').</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lower-case country code of the server address from geoip.dat (for the flag); null until resolved.
    /// Not part of the link. A flag emoji in <see cref="Name"/> takes precedence.
    /// </summary>
    public string? CountryCode { get; set; }

    public string Protocol { get; set; } = "vless";

    public required string Address { get; set; }

    public required int Port { get; set; }

    /// <summary>VLESS user id (UUID). Secret: never log it.</summary>
    public required string UserId { get; set; }

    /// <summary>VLESS encryption, "none" unless the server enables VLESS encryption.</summary>
    public string Encryption { get; set; } = "none";

    /// <summary>XTLS flow, e.g. "xtls-rprx-vision".</summary>
    public string? Flow { get; set; }

    /// <summary>Transport: tcp/raw, xhttp, ws, grpc, httpupgrade.</summary>
    public string Network { get; set; } = "tcp";

    /// <summary>none, tls or reality.</summary>
    public string Security { get; set; } = "none";

    public string? Sni { get; set; }

    /// <summary>uTLS fingerprint, e.g. "chrome".</summary>
    public string? Fingerprint { get; set; }

    public IReadOnlyList<string> Alpn { get; set; } = [];

    /// <summary>
    /// Legacy "allowInsecure" link flag. Kept only for export: Xray removed the option
    /// (since 26.x) in favor of <see cref="PinnedPeerCertSha256"/> / <see cref="VerifyPeerCertByName"/>,
    /// so it is never written to the core config and certificates are always verified.
    /// </summary>
    public bool AllowInsecure { get; set; }

    /// <summary>"pcs": comma-separated SHA-256 hashes of pinned server certificates.</summary>
    public string? PinnedPeerCertSha256 { get; set; }

    /// <summary>"vcn": name to verify the server certificate against (instead of the SNI).</summary>
    public string? VerifyPeerCertByName { get; set; }

    // Reality
    public string? RealityPublicKey { get; set; }
    public string? RealityShortId { get; set; }
    public string? RealitySpiderX { get; set; }

    /// <summary>ML-DSA-65 verification key for Reality ("pqv").</summary>
    public string? RealityMldsa65Verify { get; set; }

    // Transport settings
    public string? Path { get; set; }
    public string? Host { get; set; }

    /// <summary>XHTTP mode: auto, packet-up, stream-up, stream-one.</summary>
    public string? XhttpMode { get; set; }

    /// <summary>XHTTP "extra" object as raw JSON.</summary>
    public string? XhttpExtra { get; set; }

    public string? GrpcServiceName { get; set; }

    public string? HeaderType { get; set; }

    /// <summary>Link parameters this version does not understand, preserved for export.</summary>
    public IReadOnlyDictionary<string, string> ExtraParameters { get; set; } = new Dictionary<string, string>();

    /// <summary>Name for the UI: flag emoji removed (the flag is shown as a picture); address:port when empty.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Address}:{Port}" : Geo.CountryFlags.StripFlags(Name);

    /// <summary>Country for the flag: a flag emoji in the name wins, otherwise the geoip result.</summary>
    [JsonIgnore]
    public string? Country => Geo.CountryFlags.CodeFromName(Name) ?? CountryCode;
}
