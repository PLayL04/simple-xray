using SimpleVpn.Core.Connection;

namespace SimpleVpn.Desktop.Platform;

/// <summary>
/// M0 placeholder that only simulates state transitions so the UI shell can be exercised.
/// TODO(M1): delete once the real ConnectionManager exists.
/// </summary>
internal sealed class StubConnectionManager : IConnectionManager
{
    private static readonly TimeSpan SimulatedDelay = TimeSpan.FromMilliseconds(600);

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? LastError => null;

    public event EventHandler<ConnectionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Connecting);
        await Task.Delay(SimulatedDelay, cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Connected);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnecting);
        await Task.Delay(SimulatedDelay, cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Disconnected);
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
