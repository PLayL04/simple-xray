namespace SimpleVpn.Core.Models;

/// <summary>
/// OS-level proxy configuration. Used both to apply our proxy and to remember
/// what the user had before, so it can be restored exactly.
/// </summary>
public sealed record SystemProxySettings
{
    public bool Enabled { get; init; }

    /// <summary>"host:port" or WinINet per-protocol syntax ("http=...;https=...").</summary>
    public string? Server { get; init; }

    /// <summary>Bypass list, ';'-separated.</summary>
    public string? Bypass { get; init; }

    /// <summary>PAC script URL (takes precedence over the manual proxy in WinINet).</summary>
    public string? AutoConfigUrl { get; init; }

    /// <summary>WPAD auto-detection (takes precedence over PAC and the manual proxy).</summary>
    public bool AutoDetect { get; init; }

    /// <summary>Default bypass list: loopback, private networks and plain host names.</summary>
    public const string DefaultBypass =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;" +
        "172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";
}
