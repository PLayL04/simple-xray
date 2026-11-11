using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.ViewModels;

/// <summary>Main screen: the power button, status, server selection and the "add server" panel.</summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionManager _connection;
    private readonly ProfileRepository _profiles;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _ui;
    private bool _syncingSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(IsError), nameof(StatusText), nameof(ActionText))]
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

    // Placeholder until routing presets (M2) exist.
    [ObservableProperty]
    private string _presetName = Loc.Get("Preset_Global");

    public MainViewModel(IConnectionManager connection, ProfileRepository profiles, IClipboardService clipboard, IUiDispatcher ui)
    {
        _connection = connection;
        _profiles = profiles;
        _clipboard = clipboard;
        _ui = ui;
        _state = connection.State;
        _connection.StateChanged += OnStateChanged;
        _profiles.Changed += OnProfilesChanged;
        ReloadServers();
    }

    public ObservableCollection<ServerProfile> Servers { get; } = [];

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
    });

    private void OnProfilesChanged(object? sender, EventArgs e) => _ui.Post(ReloadServers);

    public void Dispose()
    {
        _connection.StateChanged -= OnStateChanged;
        _profiles.Changed -= OnProfilesChanged;
    }
}
