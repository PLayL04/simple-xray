using System.Globalization;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Geo;
using SimpleVpn.Core.Net;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.Tests.ViewModels;

public sealed class MainViewModelTests : IDisposable
{
    private const string Link =
        "vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@vpn.example.com:443?type=xhttp&security=tls&sni=vpn.example.com#NL";

    private const string OtherLink =
        "vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@203.0.113.10:443?type=tcp&security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw#DE";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplevpn-vm-");
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly SettingsStore _settings;
    private readonly PresetRepository _presets;
    private readonly FakeConnectionManager _connection = new();
    private readonly FakeClipboard _clipboard = new();

    public MainViewModelTests()
    {
        Loc.SetCulture(CultureInfo.GetCultureInfo("ru"));
        _paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
        _profiles = new ProfileRepository(_paths);
        _settings = new SettingsStore(_paths);
        _presets = new PresetRepository(_paths, _settings, NullLogger<PresetRepository>.Instance);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private GeoSettingsViewModel Geo() => new(
        new GeoUpdater(_paths, _settings, _presets, _connection, new HttpDownloader(_connection), NullLogger<GeoUpdater>.Instance),
        _settings,
        new InlineDispatcher());

    private MainViewModel CreateViewModel()
    {
        var dispatcher = new InlineDispatcher();
        var settings = new SettingsViewModel(_presets, _settings, _connection, new HttpDownloader(_connection), new FakeFiles(), dispatcher, _paths, Geo());
        return new(_connection, _profiles, _presets, _clipboard, dispatcher, settings);
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

    [Fact]
    public void Presets_ShowBuiltInsWithDefaultSelected()
    {
        using var vm = CreateViewModel();

        Assert.Equal(["Всё через прокси", "Всё, кроме RU", "Только заблокированное"], vm.Presets.Select(p => p.Name));
        Assert.Equal(BuiltInPresets.BypassRuId, vm.SelectedPreset?.Id);
    }

    [Fact]
    public async Task SwitchingPreset_SavesSelectionAndReconnects()
    {
        using var vm = CreateViewModel();

        vm.SelectedPreset = vm.Presets.First(p => p.Id == BuiltInPresets.GlobalId);
        await Task.Yield();

        Assert.Equal(BuiltInPresets.GlobalId, _presets.Selected.Id);
        Assert.Equal(BuiltInPresets.GlobalId, new SettingsStore(_paths).Settings.SelectedPresetId);
        Assert.Equal(1, _connection.ReconnectCalls);
        Assert.Equal(BuiltInPresets.GlobalId, vm.SelectedPreset?.Id);
    }

    [Fact]
    public void PresetChangedInSettings_IsReflectedOnMainScreen()
    {
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);

        vm.Settings.SelectedPreset = vm.Settings.Presets.First(p => p.Id == BuiltInPresets.BlockedOnlyId);
        vm.Settings.ActivateCommand.Execute(null);

        Assert.Equal(BuiltInPresets.BlockedOnlyId, vm.SelectedPreset?.Id);
        Assert.True(vm.Settings.Presets.Single(p => p.Id == BuiltInPresets.BlockedOnlyId).IsActive);
    }

    [Fact]
    public void Settings_OpenAndBack()
    {
        using var vm = CreateViewModel();

        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsOpen);

        vm.Settings.CloseCommand.Execute(null);
        Assert.False(vm.IsSettingsOpen);
    }
}
