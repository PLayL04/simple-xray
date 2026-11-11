namespace SimpleVpn.Core.Models;

/// <summary>
/// One proxy server imported from a share link. Field names follow the vless:// link
/// parameters (see <see cref="Parsing.VlessLinkParser"/>), values are kept as given
/// so the profile can be exported back to an equivalent link.
/// </summary>
public sealed record ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name (link fragment after '#').</summary>
    public string Name { get; init; } = string.Empty;

    public string Protocol { get; init; } = "vless";

    public required string Address { get; init; }

    public required int Port { get; init; }

    /// <summary>VLESS user id (UUID). Secret: never log it.</summary>
    public required string UserId { get; init; }

    /// <summary>VLESS encryption, "none" unless the server enables VLESS encryption.</summary>
    public string Encryption { get; init; } = "none";

    /// <summary>XTLS flow, e.g. "xtls-rprx-vision".</summary>
    public string? Flow { get; init; }

    /// <summary>Transport: tcp/raw, xhttp, ws, grpc, httpupgrade.</summary>
    public string Network { get; init; } = "tcp";

    /// <summary>none, tls or reality.</summary>
    public string Security { get; init; } = "none";

    public string? Sni { get; init; }

    /// <summary>uTLS fingerprint, e.g. "chrome".</summary>
    public string? Fingerprint { get; init; }

    public IReadOnlyList<string> Alpn { get; init; } = [];

    /// <summary>
    /// Legacy "allowInsecure" link flag. Kept only for export: Xray removed the option
    /// (since 26.x) in favor of <see cref="PinnedPeerCertSha256"/> / <see cref="VerifyPeerCertByName"/>,
    /// so it is never written to the core config and certificates are always verified.
    /// </summary>
    public bool AllowInsecure { get; init; }

    /// <summary>"pcs": comma-separated SHA-256 hashes of pinned server certificates.</summary>
    public string? PinnedPeerCertSha256 { get; init; }

    /// <summary>"vcn": name to verify the server certificate against (instead of the SNI).</summary>
    public string? VerifyPeerCertByName { get; init; }

    // Reality
    public string? RealityPublicKey { get; init; }
    public string? RealityShortId { get; init; }
    public string? RealitySpiderX { get; init; }

    /// <summary>ML-DSA-65 verification key for Reality ("pqv").</summary>
    public string? RealityMldsa65Verify { get; init; }

    // Transport settings
    public string? Path { get; init; }
    public string? Host { get; init; }

    /// <summary>XHTTP mode: auto, packet-up, stream-up, stream-one.</summary>
    public string? XhttpMode { get; init; }

    /// <summary>XHTTP "extra" object as raw JSON.</summary>
    public string? XhttpExtra { get; init; }

    public string? GrpcServiceName { get; init; }

    public string? HeaderType { get; init; }

    /// <summary>Link parameters this version does not understand, preserved for export.</summary>
    public IReadOnlyDictionary<string, string> ExtraParameters { get; init; } = new Dictionary<string, string>();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Address}:{Port}" : Name;
}
