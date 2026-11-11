namespace SimpleVpn.Core.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>UI language ("ru" / "en"); null = follow the OS.</summary>
    public string? Language { get; init; }

    /// <summary>Preferred local SOCKS port. A free port is chosen automatically when it is busy.</summary>
    public int SocksPort { get; init; } = 10888;

    /// <summary>Preferred local HTTP port (used for the Windows system proxy).</summary>
    public int HttpPort { get; init; } = 10889;
}
