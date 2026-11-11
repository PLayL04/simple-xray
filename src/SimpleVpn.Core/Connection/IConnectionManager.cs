namespace SimpleVpn.Core.Connection;

/// <summary>
/// Owns the connection lifecycle: config generation, core process, system proxy / TUN.
/// Implementations must roll back already applied steps when connecting fails.
/// </summary>
public interface IConnectionManager
{
    ConnectionState State { get; }

    /// <summary>Reason of the last failure when <see cref="State"/> is <see cref="ConnectionState.Error"/>.</summary>
    ConnectionError? LastError { get; }

    /// <summary>Raised on any state change. May be raised on a background thread.</summary>
    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Connects using the selected profile. Failures are reported via <see cref="State"/>, not exceptions.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reconnects if currently connected (e.g. after the selected server changed); otherwise does nothing.</summary>
    Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default);
}
