using SimpleVpn.App.ViewModels;
using SimpleVpn.Core.Connection;

namespace SimpleVpn.App.Tests.ViewModels;

public sealed class MainViewModelTests
{
    private sealed class FakeConnectionManager : IConnectionManager
    {
        public ConnectionState State { get; private set; }

        public string? LastError { get; set; }

        public int ConnectCalls { get; private set; }

        public int DisconnectCalls { get; private set; }

        public event EventHandler<ConnectionState>? StateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            Raise(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            Raise(ConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public void Raise(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    [Fact]
    public async Task Toggle_WhenDisconnected_Connects_AndWhenConnected_Disconnects()
    {
        var connection = new FakeConnectionManager();
        using var vm = new MainViewModel(connection);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, connection.ConnectCalls);
        Assert.True(vm.IsConnected);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, connection.DisconnectCalls);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public void Toggle_IsDisabledWhileBusy()
    {
        var connection = new FakeConnectionManager();
        using var vm = new MainViewModel(connection);

        connection.Raise(ConnectionState.Connecting);

        Assert.True(vm.IsBusy);
        Assert.False(vm.ToggleConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Error_ShowsReportedReason()
    {
        var connection = new FakeConnectionManager { LastError = "boom" };
        using var vm = new MainViewModel(connection);

        connection.Raise(ConnectionState.Error);

        Assert.Equal("boom", vm.StatusText);
    }
}
