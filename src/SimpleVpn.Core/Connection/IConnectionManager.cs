namespace SimpleVpn.Core.Connection;

/// <summary>
/// Owns the connection lifecycle: config generation, core process, system proxy / TUN.
/// Implementations must roll back already applied steps when connecting fails.
/// </summary>
public interface IConnectionManager
{
    ConnectionState State { get; }

    /// <summary>User-presentable reason of the last failure, if <see cref="State"/> is <see cref="ConnectionState.Error"/>.</summary>
    string? LastError { get; }

    /// <summary>Raised on any state change. May be raised on a background thread.</summary>
    event EventHandler<ConnectionState>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
