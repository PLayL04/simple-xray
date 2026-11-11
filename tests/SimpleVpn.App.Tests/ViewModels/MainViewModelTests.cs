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
    private readonly FakeElevation _elevation = new();
    private readonly FakeLifetime _lifetime = new();
    private readonly FakeAutostart _autostart = new();
    private readonly FakeFiles _files = new();
    private readonly FakeScreen _screen = new();
    private StartupOptions _startup = new();

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
        var settings = new SettingsViewModel(_presets, _settings, _connection, new HttpDownloader(_connection), new FakeFiles(), dispatcher, _paths, Geo(), _autostart,
            new ServerListViewModel(_profiles, _connection, _clipboard, dispatcher),
            new LogViewerViewModel(_paths, _clipboard, _files));
        return new(_connection, _profiles, _presets, _clipboard, dispatcher, settings, _settings, _elevation, _lifetime, _startup, _files, _screen);
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

    [Fact]
    public void ElevationRequired_OpensPrompt_RestartShutsDown()
    {
        using var vm = CreateViewModel();

        _connection.LastError = new ConnectionError(ConnectionErrorCode.ElevationRequired);
        _connection.Raise(ConnectionState.Error);
        Assert.True(vm.IsElevationPromptOpen);

        _elevation.Accept = true;
        vm.RestartElevatedCommand.Execute(null);

        Assert.Equal(1, _elevation.RestartCalls);
        Assert.True(_lifetime.ShutdownCalled);
        Assert.False(vm.IsElevationPromptOpen);
    }

    [Fact]
    public void ElevationDeclined_KeepsPromptWithMessage()
    {
        using var vm = CreateViewModel();
        _connection.LastError = new ConnectionError(ConnectionErrorCode.ElevationRequired);
        _connection.Raise(ConnectionState.Error);

        vm.RestartElevatedCommand.Execute(null);

        Assert.False(_lifetime.ShutdownCalled);
        Assert.True(vm.IsElevationPromptOpen);
        Assert.Equal(Loc.Get("Elevation_Declined"), vm.ElevationMessage);
    }

    [Fact]
    public async Task UseProxyMode_SwitchesModeAndConnects()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        _settings.Update(s => s with { Mode = Core.Models.ConnectionMode.Tun });
        using var vm = CreateViewModel();

        await vm.UseProxyModeCommand.ExecuteAsync(null);

        Assert.Equal(Core.Models.ConnectionMode.SystemProxy, new SettingsStore(_paths).Settings.Mode);
        Assert.Equal(1, _connection.ConnectCalls);
        Assert.False(vm.Settings.IsTunMode);
    }

    [Fact]
    public async Task ConnectOnStart_ConnectsWhenWindowShown()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        _startup = new StartupOptions { ConnectOnStart = true };
        using var vm = CreateViewModel();

        await vm.OnStartedAsync();

        Assert.Equal(1, _connection.ConnectCalls);
    }

    [Fact]
    public async Task WithoutConnectOnStart_DoesNotConnect()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();

        await vm.OnStartedAsync();

        Assert.Equal(0, _connection.ConnectCalls);
    }

    [Fact]
    public void Settings_TunToggle_SavesModeAndReconnects()
    {
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);

        vm.Settings.IsTunMode = true;

        Assert.Equal(Core.Models.ConnectionMode.Tun, new SettingsStore(_paths).Settings.Mode);
        Assert.Equal(1, _connection.ReconnectCalls);
        Assert.False(vm.Settings.IsSystemProxyMode);
    }

    [Fact]
    public void FirstClose_ShowsTrayHint_ThenHides()
    {
        using var vm = CreateViewModel();
        var hidden = 0;
        vm.HideRequested += (_, _) => hidden++;

        vm.OnCloseRequested();
        Assert.True(vm.IsTrayHintOpen);
        Assert.Equal(0, hidden);

        vm.AcknowledgeTrayHintCommand.Execute(null);
        Assert.False(vm.IsTrayHintOpen);
        Assert.Equal(1, hidden);
        Assert.True(new SettingsStore(_paths).Settings.TrayHintShown);

        vm.OnCloseRequested();
        Assert.False(vm.IsTrayHintOpen);
        Assert.Equal(2, hidden);
    }

    [Fact]
    public void ElevationRequired_AsksToShowWindow()
    {
        using var vm = CreateViewModel();
        var shown = 0;
        vm.ShowRequested += (_, _) => shown++;

        _connection.LastError = new ConnectionError(ConnectionErrorCode.ElevationRequired);
        _connection.Raise(ConnectionState.Error);

        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task ConnectOnStartSetting_ConnectsAfterStart()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        _settings.Update(s => s with { ConnectOnStart = true });
        using var vm = CreateViewModel();

        await vm.OnStartedAsync();

        Assert.Equal(1, _connection.ConnectCalls);
    }

    [Fact]
    public async Task ConnectOnStart_WithoutServers_DoesNothing()
    {
        _settings.Update(s => s with { ConnectOnStart = true });
        using var vm = CreateViewModel();

        await vm.OnStartedAsync();

        Assert.Equal(0, _connection.ConnectCalls);
    }

    [Fact]
    public void Exit_ShutsDown()
    {
        using var vm = CreateViewModel();

        vm.ExitCommand.Execute(null);

        Assert.True(_lifetime.ShutdownCalled);
    }

    [Fact]
    public void TrayToolTip_FollowsState()
    {
        using var vm = CreateViewModel();

        _connection.Raise(ConnectionState.Connected);

        Assert.Contains(Loc.Get("Status_Connected"), vm.TrayToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_AutostartAndConnectOnStart_AreSaved()
    {
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);

        vm.Settings.Autostart = true;
        vm.Settings.ConnectOnStart = true;

        Assert.True(_autostart.IsEnabled);
        Assert.True(new SettingsStore(_paths).Settings.ConnectOnStart);

        vm.Settings.Autostart = false;
        Assert.False(_autostart.IsEnabled);
    }

    [Fact]
    public async Task QrFromImage_AddsServer()
    {
        using var vm = CreateViewModel();
        await vm.ShowAddServerCommand.ExecuteAsync(null);
        _files.Image = QrImages.Render(Link);

        await vm.ImportQrImageCommand.ExecuteAsync(null);

        Assert.Null(vm.AddServerError);
        Assert.False(vm.IsAddingServer);
        Assert.Single(_profiles.Profiles);
    }

    [Fact]
    public async Task ScanScreen_QrWithoutVlessLink_ShowsError()
    {
        using var vm = CreateViewModel();
        await vm.ShowAddServerCommand.ExecuteAsync(null);
        _screen.Image = QrImages.Render("https://example.com/");

        await vm.ScanScreenCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Get("AddServer_QrNotVless"), vm.AddServerError);
        Assert.Empty(_profiles.Profiles);
    }

    [Fact]
    public async Task ScanScreen_NothingFound_ShowsError()
    {
        using var vm = CreateViewModel();
        var blank = new byte[100 * 100 * 4];
        Array.Fill(blank, (byte)255);
        _screen.Image = new Core.Qr.RawImage(blank, 100, 100);

        await vm.ScanScreenCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Get("AddServer_QrNotFound"), vm.AddServerError);
    }

    [Fact]
    public void Servers_RenameKeepsActiveServer()
    {
        var first = _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        var second = _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link.Replace("vpn.example.com", "two.example.com", StringComparison.Ordinal)));
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);
        var servers = vm.Settings.Servers;
        servers.Selected = servers.Servers.First(s => s.Id == first.Id);

        servers.StartRenameCommand.Execute(null);
        servers.RenameText = "  Home  ";
        servers.SaveRenameCommand.Execute(null);

        Assert.Equal("Home", _profiles.Profiles.First(p => p.Id == first.Id).Name);
        Assert.Equal(second.Id, _profiles.Selected?.Id);
        Assert.Equal(first.Id, servers.Selected?.Id);
    }

    [Fact]
    public async Task Servers_ShowQrAndCopyLink_ExportTheLink()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);
        var servers = vm.Settings.Servers;

        servers.ShowQrCommand.Execute(null);
        Assert.True(servers.IsQrOpen);
        var text = Assert.Single(Core.Qr.QrCodec.DecodeAll(QrImages.Render(servers.Qr!)));
        Assert.Equal(Core.Parsing.VlessLinkParser.Parse(Link).UserId, Core.Parsing.VlessLinkParser.Parse(text).UserId);

        await servers.CopyLinkCommand.ExecuteAsync(null);
        Assert.False(servers.IsQrOpen);
        Assert.StartsWith("vless://", _clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servers_DeleteActiveWhileConnected_DisconnectsFirst()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        vm.OpenSettingsCommand.Execute(null);
        var servers = vm.Settings.Servers;

        servers.DeleteCommand.Execute(null);
        Assert.True(servers.IsConfirmingDelete);
        await servers.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, _connection.DisconnectCalls);
        Assert.Empty(_profiles.Profiles);
        Assert.False(servers.HasServers);
    }

    [Fact]
    public async Task Log_ShowsTailOfCurrentAndPreviousRun()
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        await File.WriteAllLinesAsync(Path.Combine(_paths.LogsDirectory, "simplevpn.log"),
            Enumerable.Range(1, LogViewerViewModel.MaxLines + 20).Select(i => $"line {i}"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_paths.LogsDirectory, "simplevpn.1.log"), "old run\n", TestContext.Current.CancellationToken);
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);
        var log = vm.Settings.Log;

        log.OpenCommand.Execute(null);
        Assert.True(log.IsOpen);
        var lines = log.Text.Split(Environment.NewLine);
        Assert.Equal(LogViewerViewModel.MaxLines, lines.Length);
        Assert.Equal($"line {LogViewerViewModel.MaxLines + 20}", lines[^1]);

        log.ShowPrevious = true;
        Assert.Equal("old run", log.Text);

        await log.CopyCommand.ExecuteAsync(null);
        Assert.Equal("old run", _clipboard.Text);

        await log.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(_paths.LogsDirectory, _files.OpenedFolder);
    }

    [Fact]
    public void Log_NoFile_ShowsEmptyText()
    {
        using var vm = CreateViewModel();
        vm.OpenSettingsCommand.Execute(null);

        vm.Settings.Log.OpenCommand.Execute(null);

        Assert.Equal(Loc.Get("Log_Empty"), vm.Settings.Log.Text);
    }

    /// <summary>
    /// Regression: picking a server cleared and refilled the list right away, and the combo box showed nothing.
    /// The list must stay untouched when only the selection changed.
    /// </summary>
    [Fact]
    public void SwitchingServer_DoesNotRebuildList()
    {
        _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        var second = _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link.Replace("vpn.example.com", "two.example.com", StringComparison.Ordinal)));
        using var vm = CreateViewModel();
        var first = vm.Servers.First(s => s.Id != second.Id);
        var listChanges = 0;
        vm.Servers.CollectionChanged += (_, _) => listChanges++;

        vm.SelectedServer = first;

        Assert.Equal(first.Id, _profiles.Selected?.Id);
        Assert.Equal(0, listChanges);
        Assert.Same(first, vm.SelectedServer);
    }

    [Fact]
    public void RenamingSelectedServer_ReplacesOnlyThatItemAndKeepsSelection()
    {
        var server = _profiles.AddOrUpdate(Core.Parsing.VlessLinkParser.Parse(Link));
        using var vm = CreateViewModel();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Servers.CollectionChanged += (_, e) => actions.Add(e.Action);

        _profiles.Rename(server.Id, "Renamed");

        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Replace], actions);
        Assert.Equal("Renamed", vm.SelectedServer?.Name);
    }

    [Fact]
    public void ServerProfile_ToString_HidesAccessKey()
    {
        var profile = Core.Parsing.VlessLinkParser.Parse(Link);

        Assert.DoesNotContain(profile.UserId, profile.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchingPreset_DoesNotRebuildList()
    {
        using var vm = CreateViewModel();
        var target = vm.Presets.First(p => p.Id != vm.SelectedPreset!.Id);
        var listChanges = 0;
        vm.Presets.CollectionChanged += (_, _) => listChanges++;

        vm.SelectedPreset = target;

        Assert.Equal(0, listChanges);
        Assert.Same(target, vm.SelectedPreset);
    }
}
