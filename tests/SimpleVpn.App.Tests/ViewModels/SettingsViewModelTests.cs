using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleVpn.App.Localization;
using SimpleVpn.App.ViewModels;
using SimpleVpn.Core.Geo;
using SimpleVpn.Core.Net;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplevpn-settings-vm-");
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly PresetRepository _presets;
    private readonly FakeConnectionManager _connection = new();
    private readonly FakeFiles _files = new();
    private readonly FakeAutostart _autostart = new();

    public SettingsViewModelTests()
    {
        Loc.SetCulture(CultureInfo.GetCultureInfo("ru"));
        _paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
        _settings = new SettingsStore(_paths);
        _presets = new PresetRepository(_paths, _settings, NullLogger<PresetRepository>.Instance);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private GeoSettingsViewModel Geo() => new(
        new GeoUpdater(_paths, _settings, _presets, _connection, new HttpDownloader(_connection), NullLogger<GeoUpdater>.Instance),
        _settings,
        new InlineDispatcher());

    private SettingsViewModel Create() =>
        new(_presets, _settings, _connection, new HttpDownloader(_connection), _files, new InlineDispatcher(), _paths, Geo(), _autostart,
            new ServerListViewModel(new ProfileRepository(_paths), _connection, new FakeClipboard(), new InlineDispatcher()),
            new LogViewerViewModel(_paths, new FakeClipboard(), _files));

    private static void Select(SettingsViewModel vm, string id) => vm.SelectedPreset = vm.Presets.First(p => p.Id == id);

    [Fact]
    public void BuiltInPreset_CannotBeDeletedOrEdited_ButCanBeViewedAndDuplicated()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.GlobalId);

        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.DuplicateCommand.CanExecute(null));

        vm.EditCommand.Execute(null);

        Assert.True(vm.IsEditing);
        Assert.True(vm.Editor!.IsReadOnly);
        Assert.False(vm.Editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Duplicate_OpensEditableCopy()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.BypassRuId);

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(4, vm.Presets.Count);
        Assert.Equal("Всё, кроме RU (копия)", vm.SelectedPreset?.Name);
        Assert.False(vm.Editor!.IsReadOnly);
        Assert.Equal(BuiltInPresets.Default.Rules.Count, vm.Editor.Rules.Count);
    }

    [Fact]
    public async Task Editor_SaveActivePreset_PersistsAndReconnects()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.GlobalId);
        vm.DuplicateCommand.Execute(null);
        var id = vm.SelectedPreset!.Id;
        await vm.ActivateCommand.ExecuteAsync(null);
        var reconnects = _connection.ReconnectCalls;
        var editor = vm.Editor!;

        editor.Name = "Работа";
        editor.AddRuleCommand.Execute(null);
        editor.Rules[^1].Domains = "domain:corp.example\n\n  full:intranet.example  ";
        editor.Rules[^1].OutboundIndex = (int)RouteOutbound.Direct;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        var saved = _presets.Find(id)!;
        Assert.Equal("Работа", saved.Name);
        Assert.Equal(["domain:corp.example", "full:intranet.example"], saved.Rules[^1].Domain);
        Assert.Equal(RouteOutbound.Direct, saved.Rules[^1].Outbound);
        Assert.Equal(reconnects + 1, _connection.ReconnectCalls);
    }

    [Fact]
    public async Task Editor_InvalidRule_ShowsErrorWithRuleNumberAndKeepsEditorOpen()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.GlobalId);
        vm.DuplicateCommand.Execute(null);
        var editor = vm.Editor!;

        editor.AddRuleCommand.Execute(null);
        editor.Rules[^1].Port = "99999";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("Правило 2: Неверный порт: 99999", editor.Error);
    }

    [Fact]
    public void Editor_MoveAndRemoveRules()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.BypassRuId);
        vm.DuplicateCommand.Execute(null);
        var editor = vm.Editor!;
        var second = editor.Rules[1];

        second.MoveUpCommand.Execute(null);
        Assert.Same(second, editor.Rules[0]);
        Assert.Equal(1, second.Number);
        Assert.False(second.MoveUpCommand.CanExecute(null));

        second.RemoveCommand.Execute(null);
        Assert.Equal(BuiltInPresets.Default.Rules.Count - 1, editor.Rules.Count);
    }

    [Fact]
    public void Editor_JsonModeRoundTrip()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.GlobalId);
        vm.DuplicateCommand.Execute(null);
        var editor = vm.Editor!;

        editor.ToggleJsonCommand.Execute(null);
        Assert.True(editor.IsJsonMode);
        Assert.Contains("geoip:private", editor.JsonText, StringComparison.Ordinal);

        editor.JsonText = """{ "name": "Из JSON", "rules": [ { "ip": ["geoip:ru"], "outbound": "direct" } ] }""";
        editor.ToggleJsonCommand.Execute(null);

        Assert.False(editor.IsJsonMode);
        Assert.Equal("Из JSON", editor.Name);
        Assert.Equal("geoip:ru", Assert.Single(editor.Rules).Ips);

        editor.ToggleJsonCommand.Execute(null);
        editor.JsonText = "{ broken";
        editor.ToggleJsonCommand.Execute(null);
        Assert.True(editor.IsJsonMode);
        Assert.NotNull(editor.Error);
    }

    [Fact]
    public async Task DeleteUserPreset_RequiresConfirmation()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.GlobalId);
        vm.DuplicateCommand.Execute(null);
        vm.CloseCommand.Execute(null);
        var id = vm.SelectedPreset!.Id;

        vm.DeleteCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.NotNull(_presets.Find(id));

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Null(_presets.Find(id));
        Assert.Equal(3, vm.Presets.Count);
    }

    [Fact]
    public async Task ImportFromFile_AddsPresetWithoutActivatingIt()
    {
        using var vm = Create();
        _files.OpenResult = """{ "name": "От админа", "rules": [ { "domain": ["geosite:ru-blocked"] } ], "defaultOutbound": "direct" }""";

        await vm.ImportFileCommand.ExecuteAsync(null);

        Assert.Equal("От админа", vm.SelectedPreset?.Name);
        Assert.False(vm.SelectedPreset!.IsActive);
        Assert.Equal(BuiltInPresets.DefaultId, _presets.Selected.Id);
        Assert.False(vm.IsPresetMessageError);
        Assert.True(vm.ActivateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ImportFromFile_Invalid_ShowsLocalizedError()
    {
        using var vm = Create();
        _files.OpenResult = "not a preset";

        await vm.ImportFileCommand.ExecuteAsync(null);

        Assert.True(vm.IsPresetMessageError);
        Assert.Equal(Loc.Get("PresetError_Malformed"), vm.PresetMessage);
        Assert.Equal(3, vm.Presets.Count);
    }

    [Fact]
    public async Task ImportUrl_RejectsNonHttpUrl()
    {
        using var vm = Create();
        vm.ShowImportUrlCommand.Execute(null);
        vm.ImportUrl = "ftp://example.org/p.json";

        await vm.ImportUrlCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Get("PresetError_InvalidUrl"), vm.PresetMessage);
        Assert.True(vm.IsImportingUrl);
    }

    [Fact]
    public async Task Export_WritesPresetJson()
    {
        using var vm = Create();
        Select(vm, BuiltInPresets.BlockedOnlyId);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Только заблокированное.json", _files.SavedName);
        Assert.Equal(BuiltInPresets.BlockedOnlyId, PresetSerializer.Parse(_files.Saved!).Id);
    }

    [Fact]
    public async Task ApplyAdvanced_ValidatesAndSaves()
    {
        using var vm = Create();

        vm.HttpPort = vm.SocksPort;
        await vm.ApplyAdvancedCommand.ExecuteAsync(null);
        Assert.True(vm.IsAdvancedMessageError);

        vm.HttpPort = 20889;
        vm.DirectDns = "dns.example";
        await vm.ApplyAdvancedCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("Settings_InvalidDirectDns"), vm.AdvancedMessage);

        vm.DirectDns = "77.88.8.8";
        vm.PreferIPv4 = false;
        await vm.ApplyAdvancedCommand.ExecuteAsync(null);

        Assert.False(vm.IsAdvancedMessageError);
        var saved = new SettingsStore(_paths).Settings;
        Assert.Equal(20889, saved.HttpPort);
        Assert.Equal("77.88.8.8", saved.Dns.DirectServer);
        Assert.False(saved.Dns.PreferIPv4);
        Assert.Equal(1, _connection.ReconnectCalls);
    }

    [Fact]
    public void Language_IsSavedAndAsksForRestart()
    {
        using var vm = Create();
        Assert.Equal(0, vm.LanguageIndex);

        vm.LanguageIndex = 2;

        Assert.Equal("en", new SettingsStore(_paths).Settings.Language);
        Assert.True(vm.IsLanguageChanged);
    }

    [Fact]
    public void Geo_IntervalAndAutoUpdateAreSaved()
    {
        using var vm = Create();
        Assert.Equal(2, vm.Geo.IntervalIndex);
        Assert.Equal(Loc.Get("Geo_NeverUpdated"), vm.Geo.LastUpdateText);

        vm.Geo.IntervalIndex = 4;
        vm.Geo.AutoUpdate = false;

        var geo = new SettingsStore(_paths).Settings.Geo;
        Assert.Equal(168, geo.IntervalHours);
        Assert.False(geo.AutoUpdate);
    }

    [Fact]
    public void Geo_CustomSourceRequiresBothUrls()
    {
        using var vm = Create();
        vm.Geo.UseCustomSource = true;
        vm.Geo.CustomGeoipUrl = "https://example.org/geoip.dat";

        vm.Geo.SaveSourceCommand.Execute(null);
        Assert.True(vm.Geo.IsSourceMessageError);
        Assert.False(new SettingsStore(_paths).Settings.Geo.UseCustomSource);

        vm.Geo.CustomGeositeUrl = "https://example.org/geosite.dat";
        vm.Geo.SaveSourceCommand.Execute(null);
        Assert.True(new SettingsStore(_paths).Settings.Geo.UseCustomSource);

        vm.Geo.UseDefaultSource = true;
        var geo = new SettingsStore(_paths).Settings.Geo;
        Assert.False(geo.UseCustomSource);
        Assert.Equal("https://example.org/geosite.dat", geo.CustomGeositeUrl);
    }

    [Fact]
    public async Task Geo_UpdateFailureShowsLocalizedError()
    {
        using var vm = Create();
        vm.Geo.UseCustomSource = true;
        vm.Geo.CustomGeoipUrl = "http://127.0.0.1:9/geoip.dat";
        vm.Geo.CustomGeositeUrl = "http://127.0.0.1:9/geosite.dat";
        vm.Geo.SaveSourceCommand.Execute(null);

        await vm.Geo.UpdateNowCommand.ExecuteAsync(null);

        Assert.True(vm.Geo.IsError);
        Assert.StartsWith(Loc.Get("GeoError_DownloadFailed"), vm.Geo.ResultText, StringComparison.Ordinal);
        Assert.True(vm.Geo.IsIdle);
    }
}
