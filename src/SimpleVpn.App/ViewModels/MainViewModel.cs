using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Qr;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.ViewModels;

/// <summary>
/// Main screen: the power button, status, server and routing preset pickers, the "add server" panel,
/// and the entry to the settings screen.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionManager _connection;
    private readonly ProfileRepository _profiles;
    private readonly PresetRepository _presets;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _ui;
    private readonly IElevation _elevation;
    private readonly IAppLifetime _lifetime;
    private readonly SettingsStore _settingsStore;
    private readonly StartupOptions _startup;
    private readonly IFileService _files;
    private readonly IScreenCapture _screen;
    private bool _syncingSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(IsError), nameof(StatusText), nameof(ActionText), nameof(TrayToolTip))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private ConnectionState _state;

    [ObservableProperty]
    private ServerProfile? _selectedServer;

    [ObservableProperty]
    private bool _isAddingServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private string _newLink = string.Empty;

    [ObservableProperty]
    private string? _addServerError;

    [ObservableProperty]
    private PresetItem? _selectedPreset;

    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>"VPN mode needs administrator rights" dialog.</summary>
    [ObservableProperty]
    private bool _isElevationPromptOpen;

    [ObservableProperty]
    private string? _elevationMessage;

    /// <summary>One-time "SimpleVpn keeps running in the tray" dialog shown on the first window close.</summary>
    [ObservableProperty]
    private bool _isTrayHintOpen;

    public MainViewModel(
        IConnectionManager connection,
        ProfileRepository profiles,
        PresetRepository presets,
        IClipboardService clipboard,
        IUiDispatcher ui,
        SettingsViewModel settings,
        SettingsStore settingsStore,
        IElevation elevation,
        IAppLifetime lifetime,
        StartupOptions startup,
        IFileService files,
        IScreenCapture screen)
    {
        _files = files;
        _screen = screen;
        _settingsStore = settingsStore;
        _elevation = elevation;
        _lifetime = lifetime;
        _startup = startup;
        _connection = connection;
        _profiles = profiles;
        _presets = presets;
        _clipboard = clipboard;
        _ui = ui;
        Settings = settings;
        _state = connection.State;
        _connection.StateChanged += OnStateChanged;
        _profiles.Changed += OnProfilesChanged;
        _presets.Changed += OnPresetsChanged;
        Settings.CloseRequested += OnSettingsCloseRequested;
        ReloadServers();
        ReloadPresets();
    }

    /// <summary>The window should be shown and activated (e.g. a dialog needs the user while in the tray).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The window should be hidden to the tray.</summary>
    public event EventHandler? HideRequested;

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ServerProfile> Servers { get; } = [];

    /// <summary>Quick routing switch on the main screen.</summary>
    public ObservableCollection<PresetItem> Presets { get; } = [];

    public bool HasServers => Servers.Count > 0;

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;

    public bool IsError => State == ConnectionState.Error;

    public string StatusText => State switch
    {
        ConnectionState.Connecting => Loc.Get("Status_Connecting"),
        ConnectionState.Connected => Loc.Get("Status_Connected"),
        ConnectionState.Disconnecting => Loc.Get("Status_Disconnecting"),
        ConnectionState.Error => ErrorText(_connection.LastError),
        _ => Loc.Get("Status_Disconnected"),
    };

    public string ActionText => IsConnected ? Loc.Get("Action_Disconnect") : Loc.Get("Action_Connect");

    public string TrayToolTip => $"{Loc.Get("AppTitle")}: {StatusText}";

    public static string ErrorText(ConnectionError? error) =>
        error is null ? Loc.Get("Status_Error") : Loc.Get($"Error_{error.Code}");

    public static string LinkErrorText(LinkParseError error) => Loc.Get($"LinkError_{error}");

    private bool CanToggleConnection() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await _connection.DisconnectAsync();
        }
        else if (!HasServers)
        {
            await ShowAddServerAsync();
        }
        else
        {
            await _connection.ConnectAsync();
        }
    }

    /// <summary>Opens the "add server" panel, pre-filled from the clipboard when it holds a link.</summary>
    [RelayCommand]
    private async Task ShowAddServerAsync()
    {
        AddServerError = null;
        NewLink = string.Empty;
        IsAddingServer = true;

        var text = await TryGetClipboardTextAsync();
        if (VlessLinkParser.LooksLikeLink(text))
        {
            NewLink = text!.Trim();
        }
    }

    [RelayCommand]
    private void CancelAddServer()
    {
        IsAddingServer = false;
        AddServerError = null;
    }

    [RelayCommand]
    private async Task PasteLinkAsync()
    {
        var text = await TryGetClipboardTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            NewLink = text.Trim();
            AddServerError = null;
        }
    }

    private bool CanAddServer() => !string.IsNullOrWhiteSpace(NewLink);

    [RelayCommand(CanExecute = nameof(CanAddServer))]
    private async Task AddServerAsync()
    {
        ServerProfile profile;
        try
        {
            profile = VlessLinkParser.Parse(NewLink);
        }
        catch (LinkParseException ex)
        {
            AddServerError = LinkErrorText(ex.Error);
            return;
        }

        _profiles.AddOrUpdate(profile);
        IsAddingServer = false;
        NewLink = string.Empty;
        await _connection.ReconnectIfConnectedAsync();
    }

    /// <summary>Largest picture accepted for QR import.</summary>
    public const int MaxImageBytes = 20 * 1024 * 1024;

    [RelayCommand]
    private async Task ImportQrImageAsync()
    {
        AddServerError = null;
        RawImage? image;
        try
        {
            image = await _files.OpenImageAsync(Loc.Get("AddServer_ImageTitle"), MaxImageBytes);
        }
        catch (IOException)
        {
            AddServerError = Loc.Get("AddServer_ImageError");
            return;
        }

        if (image is not null)
        {
            await AddFromQrAsync(image);
        }
    }

    /// <summary>Looks for a QR code anywhere on the screen (e.g. sent in a messenger).</summary>
    [RelayCommand]
    private async Task ScanScreenAsync()
    {
        AddServerError = null;
        var image = await Task.Run(_screen.Capture);
        if (image is null)
        {
            AddServerError = Loc.Get("AddServer_QrNotFound");
            return;
        }

        await AddFromQrAsync(image);
    }

    private async Task AddFromQrAsync(RawImage image)
    {
        var texts = await Task.Run(() => QrCodec.DecodeAll(image));
        var link = texts.FirstOrDefault(VlessLinkParser.LooksLikeLink);
        if (link is null)
        {
            AddServerError = Loc.Get(texts.Count > 0 ? "AddServer_QrNotVless" : "AddServer_QrNotFound");
            return;
        }

        NewLink = link.Trim();
        await AddServerAsync();
    }

    [RelayCommand]
    private async Task RemoveSelectedServerAsync()
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        if (State is ConnectionState.Connected or ConnectionState.Error)
        {
            await _connection.DisconnectAsync();
        }

        _profiles.Remove(server.Id);
    }

    /// <summary>Called once the UI is up (the window may stay hidden when started with the OS).</summary>
    public async Task OnStartedAsync()
    {
        // After the elevated restart for VPN mode, or "connect on start" in the settings.
        var connect = _startup.ConnectOnStart || _settingsStore.Settings.ConnectOnStart;
        if (connect && HasServers && State is ConnectionState.Disconnected or ConnectionState.Error)
        {
            await _connection.ConnectAsync();
        }
    }

    /// <summary>The user closed the window: hide to the tray, the first time with an explanation.</summary>
    public void OnCloseRequested()
    {
        if (_settingsStore.Settings.TrayHintShown)
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsSettingsOpen = false;
        IsTrayHintOpen = true;
    }

    [RelayCommand]
    private void AcknowledgeTrayHint()
    {
        _settingsStore.Update(s => s with { TrayHintShown = true });
        IsTrayHintOpen = false;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Exit from the tray menu; the app disconnects on the way out.</summary>
    [RelayCommand]
    private void Exit() => _lifetime.Shutdown();

    /// <summary>Restarts the app with administrator rights (UAC); the new instance connects right away.</summary>
    [RelayCommand]
    private void RestartElevated()
    {
        if (_elevation.RestartElevated())
        {
            IsElevationPromptOpen = false;
            _lifetime.Shutdown();
            return;
        }

        ElevationMessage = Loc.Get("Elevation_Declined");
    }

    /// <summary>Gives up VPN mode for now: switches to the system proxy and connects.</summary>
    [RelayCommand]
    private async Task UseProxyModeAsync()
    {
        IsElevationPromptOpen = false;
        _settingsStore.Update(s => s with { Mode = ConnectionMode.SystemProxy });
        Settings.Refresh();
        await _connection.ConnectAsync();
    }

    [RelayCommand]
    private void CancelElevation() => IsElevationPromptOpen = false;

    [RelayCommand]
    private void OpenSettings()
    {
        Settings.Refresh();
        IsSettingsOpen = true;
    }

    private void OnSettingsCloseRequested(object? sender, EventArgs e) => IsSettingsOpen = false;

    /// <summary>Switching the preset while connected restarts the core in place (no "Disconnected" in between).</summary>
    partial void OnSelectedPresetChanged(PresetItem? value)
    {
        if (_syncingSelection || value is null || value.Id == _presets.Selected.Id)
        {
            return;
        }

        _presets.Select(value.Id);
        _ = _connection.ReconnectIfConnectedAsync();
    }

    private void ReloadPresets()
    {
        _syncingSelection = true;
        try
        {
            Presets.Clear();
            foreach (var preset in _presets.Presets)
            {
                Presets.Add(new PresetItem(preset));
            }

            var activeId = _presets.Selected.Id;
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == activeId);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => _ui.Post(ReloadPresets);

    partial void OnSelectedServerChanged(ServerProfile? value)
    {
        if (_syncingSelection || value is null || value.Id == _profiles.Selected?.Id)
        {
            return;
        }

        _profiles.Select(value.Id);
        _ = _connection.ReconnectIfConnectedAsync();
    }

    private void ReloadServers()
    {
        _syncingSelection = true;
        try
        {
            Servers.Clear();
            foreach (var profile in _profiles.Profiles)
            {
                Servers.Add(profile);
            }

            var selectedId = _profiles.Selected?.Id;
            SelectedServer = Servers.FirstOrDefault(p => p.Id == selectedId);
        }
        finally
        {
            _syncingSelection = false;
        }

        OnPropertyChanged(nameof(HasServers));
    }

    private async Task<string?> TryGetClipboardTextAsync()
    {
        try
        {
            return await _clipboard.GetTextAsync();
        }
#pragma warning disable CA1031 // Clipboard access may fail (locked by another app); not worth an error.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private void OnStateChanged(object? sender, ConnectionState state) => _ui.Post(() =>
    {
        State = state;

        // Error -> Error with a different reason does not change State; refresh the text explicitly.
        OnPropertyChanged(nameof(StatusText));

        if (state == ConnectionState.Error && _connection.LastError?.Code == ConnectionErrorCode.ElevationRequired)
        {
            ElevationMessage = null;
            IsSettingsOpen = false;
            IsElevationPromptOpen = true;
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    });

    private void OnProfilesChanged(object? sender, EventArgs e) => _ui.Post(ReloadServers);

    public void Dispose()
    {
        _connection.StateChanged -= OnStateChanged;
        _profiles.Changed -= OnProfilesChanged;
        _presets.Changed -= OnPresetsChanged;
        Settings.CloseRequested -= OnSettingsCloseRequested;
        Settings.Dispose();
    }
}
