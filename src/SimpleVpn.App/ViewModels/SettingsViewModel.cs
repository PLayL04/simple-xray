using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Net;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.ViewModels;

/// <summary>
/// Settings screen. Hierarchy: connection mode on top, routing presets inside, then general and advanced options.
/// Mode, preset and language apply immediately; advanced network options apply with a button.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private static readonly string?[] Languages = [null, "ru", "en"];

    private readonly PresetRepository _presets;
    private readonly SettingsStore _settings;
    private readonly IConnectionManager _connection;
    private readonly HttpDownloader _downloader;
    private readonly IFileService _files;
    private readonly IUiDispatcher _ui;
    private readonly AppPaths _paths;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModifySelected), nameof(HasSource))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(UpdateFromSourceCommand), nameof(ExportCommand), nameof(DuplicateCommand), nameof(EditCommand), nameof(ActivateCommand))]
    private PresetItem? _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsBrowsing))]
    private PresetEditorViewModel? _editor;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private bool _isImportingUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportUrlCommand))]
    private string _importUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportUrlCommand), nameof(UpdateFromSourceCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private string? _presetMessage;

    [ObservableProperty]
    private bool _isPresetMessageError;

    [ObservableProperty]
    private int _languageIndex;

    [ObservableProperty]
    private bool _isLanguageChanged;

    [ObservableProperty]
    private decimal? _socksPort;

    [ObservableProperty]
    private decimal? _httpPort;

    [ObservableProperty]
    private string _remoteDns = string.Empty;

    [ObservableProperty]
    private string _directDns = string.Empty;

    [ObservableProperty]
    private bool _preferIPv4;

    [ObservableProperty]
    private int _logLevelIndex;

    [ObservableProperty]
    private string? _advancedMessage;

    [ObservableProperty]
    private bool _isAdvancedMessageError;

    public SettingsViewModel(
        PresetRepository presets,
        SettingsStore settings,
        IConnectionManager connection,
        HttpDownloader downloader,
        IFileService files,
        IUiDispatcher ui,
        AppPaths paths,
        GeoSettingsViewModel geo)
    {
        Geo = geo;
        _presets = presets;
        _settings = settings;
        _connection = connection;
        _downloader = downloader;
        _files = files;
        _ui = ui;
        _paths = paths;
        _presets.Changed += OnPresetsChanged;
        ReloadPresets();
        LoadSettings();
    }

    /// <summary>Raised when the user leaves the settings screen.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<PresetItem> Presets { get; } = [];

    /// <summary>"Geo databases" section.</summary>
    public GeoSettingsViewModel Geo { get; }

    public bool IsEditing => Editor is not null;

    public bool IsBrowsing => Editor is null;

    public bool CanModifySelected => SelectedPreset is { IsBuiltIn: false };

    public bool HasSource => SelectedPreset?.Preset.SourceUrl is not null;

    public static string VersionText => Loc.Format("Settings_About", Version);

    public static string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Reloads everything from storage when the screen is opened.</summary>
    public void Refresh()
    {
        Editor = null;
        IsConfirmingDelete = false;
        IsImportingUrl = false;
        PresetMessage = null;
        AdvancedMessage = null;
        ReloadPresets();
        LoadSettings();
        Geo.Refresh();
    }

    [RelayCommand]
    private void Close()
    {
        if (Editor is not null)
        {
            Editor = null;
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedPresetChanged(PresetItem? value) => IsConfirmingDelete = false;

    private bool HasSelection() => SelectedPreset is not null;

    private bool CanActivate() => SelectedPreset is { IsActive: false };

    /// <summary>Makes the selected row the active preset (the same as picking it on the main screen).</summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        if (SelectedPreset is not { } item)
        {
            return;
        }

        _presets.Select(item.Id);
        await _connection.ReconnectIfConnectedAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (SelectedPreset is { } item)
        {
            OpenEditor(item.Preset);
        }
    }

    /// <summary>Copies the selected preset (built-in or not) and opens the copy in the editor.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (SelectedPreset is not { } item)
        {
            return;
        }

        var name = Loc.Format("Presets_CopyName", item.Name);
        var copy = RunPresetAction(() => _presets.Duplicate(item.Id, name));
        if (copy is not null)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == copy.Id) ?? SelectedPreset;
            OpenEditor(copy);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private void Delete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;
        if (SelectedPreset is not { IsBuiltIn: false } item)
        {
            return;
        }

        var wasActive = item.Id == _presets.Selected.Id;
        if (RunPresetAction(() => { _presets.Delete(item.Id); return item.Preset; }) is not null)
        {
            ShowPresetMessage(Loc.Format("Presets_Deleted", item.Name), isError: false);
            if (wasActive)
            {
                await _connection.ReconnectIfConnectedAsync();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync()
    {
        if (SelectedPreset is not { } item)
        {
            return;
        }

        try
        {
            var fileName = string.Concat(item.Name.Split(Path.GetInvalidFileNameChars())).Trim() + ".json";
            if (await _files.SaveJsonFileAsync(Loc.Get("Presets_Export"), fileName, PresetRepository.Export(item.Preset)))
            {
                ShowPresetMessage(Loc.Get("Presets_Exported"), isError: false);
            }
        }
        catch (IOException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        string? json;
        try
        {
            json = await _files.OpenJsonFileAsync(Loc.Get("Presets_ImportFile"), PresetSerializer.MaxSizeBytes);
        }
        catch (IOException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
            return;
        }

        if (json is not null)
        {
            ImportJson(json, sourceUrl: null);
        }
    }

    [RelayCommand]
    private void ShowImportUrl()
    {
        ImportUrl = string.Empty;
        IsImportingUrl = true;
    }

    [RelayCommand]
    private void CancelImportUrl() => IsImportingUrl = false;

    private bool CanImportUrl() => !IsDownloading && !string.IsNullOrWhiteSpace(ImportUrl);

    [RelayCommand(CanExecute = nameof(CanImportUrl))]
    private async Task ImportUrlAsync()
    {
        if (!HttpDownloader.IsSupportedUrl(ImportUrl, out var uri))
        {
            ShowPresetMessage(Loc.Get("PresetError_InvalidUrl"), isError: true);
            return;
        }

        var json = await DownloadAsync(uri!);
        if (json is not null && ImportJson(json, uri!.ToString()))
        {
            IsImportingUrl = false;
        }
    }

    private bool CanUpdateFromSource() => HasSource && !IsDownloading && CanModifySelected;

    [RelayCommand(CanExecute = nameof(CanUpdateFromSource))]
    private async Task UpdateFromSourceAsync()
    {
        if (SelectedPreset is not { Preset.SourceUrl: { } url } item || !HttpDownloader.IsSupportedUrl(url, out var uri))
        {
            return;
        }

        var json = await DownloadAsync(uri!);
        if (json is null)
        {
            return;
        }

        if (RunPresetAction(() => _presets.ReplaceFromSource(item.Id, json)) is not null)
        {
            ShowPresetMessage(Loc.Get("Presets_Updated"), isError: false);
            if (item.Id == _presets.Selected.Id)
            {
                await _connection.ReconnectIfConnectedAsync();
            }
        }
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_syncing)
        {
            return;
        }

        var language = Languages[Math.Clamp(value, 0, Languages.Length - 1)];
        _settings.Update(s => s with { Language = language });
        IsLanguageChanged = true;
    }

    [RelayCommand]
    private async Task ApplyAdvancedAsync()
    {
        AdvancedMessage = null;
        var socks = (int)(SocksPort ?? 0);
        var http = (int)(HttpPort ?? 0);
        if (socks is < 1024 or > 65535 || http is < 1024 or > 65535 || socks == http)
        {
            ShowAdvancedMessage(Loc.Get("Settings_InvalidPorts"), isError: true);
            return;
        }

        var remote = RemoteDns.Trim();
        if (remote.Length == 0 || remote.Any(char.IsWhiteSpace))
        {
            ShowAdvancedMessage(Loc.Get("Settings_InvalidRemoteDns"), isError: true);
            return;
        }

        var direct = DirectDns.Trim();
        if (!string.Equals(direct, DnsSettings.SystemResolver, StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(direct, out _))
        {
            ShowAdvancedMessage(Loc.Get("Settings_InvalidDirectDns"), isError: true);
            return;
        }

        var logLevel = AppSettings.LogLevels[Math.Clamp(LogLevelIndex, 0, AppSettings.LogLevels.Count - 1)];
        _settings.Update(s => s with
        {
            SocksPort = socks,
            HttpPort = http,
            CoreLogLevel = logLevel,
            Dns = new DnsSettings { RemoteServer = remote, DirectServer = direct.ToLowerInvariant(), PreferIPv4 = PreferIPv4 },
        });
        ShowAdvancedMessage(Loc.Get("Settings_Applied"), isError: false);

        // Ports apply on the next connect: changing them now would move the system proxy under the user.
        await _connection.ReconnectIfConnectedAsync();
    }

    [RelayCommand]
    private void ResetAdvanced()
    {
        var defaults = new AppSettings();
        SocksPort = defaults.SocksPort;
        HttpPort = defaults.HttpPort;
        RemoteDns = defaults.Dns.RemoteServer;
        DirectDns = defaults.Dns.DirectServer;
        PreferIPv4 = defaults.Dns.PreferIPv4;
        LogLevelIndex = IndexOfLogLevel(defaults.CoreLogLevel);
        AdvancedMessage = null;
    }

    [RelayCommand]
    private Task OpenLogsAsync() => _files.OpenFolderAsync(_paths.LogsDirectory);

    private void OpenEditor(RoutingPreset preset)
    {
        PresetMessage = null;
        var editor = new PresetEditorViewModel(preset, _presets, _connection);
        editor.Closed += (_, _) => Editor = null;
        Editor = editor;
    }

    private bool ImportJson(string json, string? sourceUrl)
    {
        var imported = RunPresetAction(() => _presets.Import(json, sourceUrl));
        if (imported is null)
        {
            return false;
        }

        ShowPresetMessage(Loc.Format("Presets_Imported", imported.Name), isError: false);

        // Only highlighted in the list; it becomes active when the user activates it.
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == imported.Id) ?? SelectedPreset;
        return true;
    }

    private async Task<string?> DownloadAsync(Uri uri)
    {
        IsDownloading = true;
        PresetMessage = Loc.Get("Presets_Downloading");
        IsPresetMessageError = false;
        try
        {
            return await _downloader.GetStringAsync(uri, PresetSerializer.MaxSizeBytes);
        }
        catch (DownloadException ex)
        {
            ShowPresetMessage($"{Loc.Get("PresetError_DownloadFailed")}: {ex.Message}", isError: true);
            return null;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private RoutingPreset? RunPresetAction(Func<RoutingPreset> action)
    {
        try
        {
            return action();
        }
        catch (PresetException ex)
        {
            ShowPresetMessage(PresetItem.ErrorText(ex), isError: true);
        }
        catch (IOException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowPresetMessage($"{Loc.Get("Presets_FileError")}: {ex.Message}", isError: true);
        }

        return null;
    }

    private void ShowPresetMessage(string text, bool isError)
    {
        PresetMessage = text;
        IsPresetMessageError = isError;
    }

    private void ShowAdvancedMessage(string text, bool isError)
    {
        AdvancedMessage = text;
        IsAdvancedMessageError = isError;
    }

    private void LoadSettings()
    {
        var s = _settings.Settings;
        _syncing = true;
        try
        {
            LanguageIndex = Math.Max(0, Array.IndexOf(Languages, s.Language));
        }
        finally
        {
            _syncing = false;
        }

        SocksPort = s.SocksPort;
        HttpPort = s.HttpPort;
        RemoteDns = s.Dns.RemoteServer;
        DirectDns = s.Dns.DirectServer;
        PreferIPv4 = s.Dns.PreferIPv4;
        LogLevelIndex = IndexOfLogLevel(s.CoreLogLevel);
    }

    private static int IndexOfLogLevel(string level) =>
        Math.Max(0, AppSettings.LogLevels.ToList().IndexOf(level.ToLowerInvariant()));

    private void ReloadPresets()
    {
        var keepId = SelectedPreset?.Id;
        var activeId = _presets.Selected.Id;
        Presets.Clear();
        foreach (var preset in _presets.Presets)
        {
            Presets.Add(new PresetItem(preset) { IsActive = preset.Id == activeId });
        }

        // Keep the row the user is working with (e.g. a freshly imported preset), else show the active one.
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == keepId) ?? Presets.FirstOrDefault(p => p.IsActive);
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => _ui.Post(ReloadPresets);

    public void Dispose()
    {
        _presets.Changed -= OnPresetsChanged;
        Geo.Dispose();
    }
}
