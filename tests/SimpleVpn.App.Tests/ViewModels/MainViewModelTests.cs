using System.Globalization;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.App.ViewModels;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private const string Link =
        "vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@vpn.example.com:443?type=xhttp&security=tls&sni=vpn.example.com#NL";

    private const string OtherLink =
        "vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@203.0.113.10:443?type=tcp&security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw#DE";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplevpn-vm-");
    private readonly ProfileRepository _profiles;
    private readonly FakeConnectionManager _connection = new();
    private readonly FakeClipboard _clipboard = new();

    public MainViewModelTests()
    {
        Loc.SetCulture(CultureInfo.GetCultureInfo("ru"));
        _profiles = new ProfileRepository(AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false));
    }

    public void Dispose() => _root.Delete(recursive: true);

    private MainViewModel CreateViewModel() => new(_connection, _profiles, _clipboard, new InlineDispatcher());

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    [Fact]
    public async Task Toggle_WithServer_ConnectsAndDisconnects()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, _connection.ConnectCalls);
        Assert.True(vm.IsConnected);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, _connection.DisconnectCalls);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public async Task Toggle_WithoutServers_OpensAddPanelPrefilledFromClipboard()
    {
        _clipboard.Text = "  " + Link + "\n";
        using var vm = CreateViewModel();

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(0, _connection.ConnectCalls);
        Assert.True(vm.IsAddingServer);
        Assert.Equal(Link, vm.NewLink);
    }

    [Fact]
    public async Task ShowAddServer_ClipboardWithoutLink_LeavesFieldEmpty()
    {
        _clipboard.Text = "some unrelated text";
        using var vm = CreateViewModel();

        await vm.ShowAddServerCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.NewLink);
    }

    [Fact]
    public async Task AddServer_ValidLink_AddsSelectsAndClosesPanel()
    {
        using var vm = CreateViewModel();
        await vm.ShowAddServerCommand.ExecuteAsync(null);
        vm.NewLink = Link;

        await vm.AddServerCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddingServer);
        Assert.True(vm.HasServers);
        Assert.Equal("NL", vm.SelectedServer?.DisplayName);
        Assert.Equal(1, _connection.ReconnectCalls);
    }

    [Fact]
    public async Task AddServer_InvalidLink_ShowsLocalizedError()
    {
        using var vm = CreateViewModel();
        await vm.ShowAddServerCommand.ExecuteAsync(null);
        vm.NewLink = "vmess://abc";

        await vm.AddServerCommand.ExecuteAsync(null);

        Assert.True(vm.IsAddingServer);
        Assert.Equal("Поддерживаются только ссылки vless://", vm.AddServerError);
        Assert.False(vm.HasServers);
    }

    [Fact]
    public async Task SelectingAnotherServer_SelectsInRepositoryAndReconnects()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        var other = _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(OtherLink));
        using var vm = CreateViewModel();
        var first = vm.Servers.First(s => s.Id != other.Id);

        vm.SelectedServer = first;
        await Task.Yield();

        Assert.Equal(first.Id, _profiles.Selected?.Id);
        Assert.Equal(1, _connection.ReconnectCalls);
    }

    [Fact]
    public async Task RemoveSelectedServer_WhileConnected_DisconnectsFirst()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();
        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        await vm.RemoveSelectedServerCommand.ExecuteAsync(null);

        Assert.Equal(1, _connection.DisconnectCalls);
        Assert.False(vm.HasServers);
        Assert.Null(vm.SelectedServer);
    }

    [Fact]
    public void Toggle_IsDisabledWhileBusy()
    {
        using var vm = CreateViewModel();

        _connection.Raise(ConnectionState.Connecting);

        Assert.True(vm.IsBusy);
        Assert.False(vm.ToggleConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Error_ShowsLocalizedReason()
    {
        using var vm = CreateViewModel();

        _connection.LastError = new ConnectionError(ConnectionErrorCode.CoreExited, "raw xray output");
        _connection.Raise(ConnectionState.Error);

        Assert.True(vm.IsError);
        Assert.StartsWith("Ядро Xray остановилось", vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; set; }

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    private sealed class FakeConnectionManager : IConnectionManager
    {
        public ConnectionState State { get; private set; }

        public ConnectionError? LastError { get; set; }

        public int ConnectCalls { get; private set; }

        public int DisconnectCalls { get; private set; }

        public int ReconnectCalls { get; private set; }

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

        public Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default)
        {
            ReconnectCalls++;
            return Task.CompletedTask;
        }

        public void Raise(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }
}
