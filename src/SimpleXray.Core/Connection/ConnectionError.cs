namespace SimpleXray.Core.Connection;

public enum ConnectionErrorCode
{
    /// <summary>No server profile selected.</summary>
    NoServer,

    /// <summary>The core binary is missing (broken installation / antivirus quarantine).</summary>
    CoreNotFound,

    /// <summary>geoip.dat / geosite.dat are missing and there is no seed copy.</summary>
    GeoFilesMissing,

    /// <summary>The core did not become ready in time.</summary>
    CoreStartTimeout,

    /// <summary>The core process exited (on start: usually an invalid config).</summary>
    CoreExited,

    /// <summary>Applying the system proxy failed.</summary>
    SystemProxyFailed,

    /// <summary>VPN (TUN) mode needs administrator rights; the UI offers to restart elevated.</summary>
    ElevationRequired,

    /// <summary>The TUN adapter did not come up (driver blocked, adapter conflict).</summary>
    TunFailed,

    Unexpected,
}

/// <param name="Code">Mapped to a localized message by the UI.</param>
/// <param name="Detail">Technical details for the log (core output, exception message). Not localized.</param>
public sealed record ConnectionError(ConnectionErrorCode Code, string? Detail = null);

internal sealed class ConnectionFailedException(ConnectionError error) : Exception(error.Detail ?? error.Code.ToString())
{
    public ConnectionError Error { get; } = error;
}
