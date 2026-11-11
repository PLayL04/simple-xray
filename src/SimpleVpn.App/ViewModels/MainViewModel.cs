using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.Core.Connection;

namespace SimpleVpn.App.ViewModels;

/// <summary>Main screen: the power button, status, current server and routing preset.</summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionManager _connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(StatusText), nameof(ActionText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private ConnectionState _state;

    public MainViewModel(IConnectionManager connection)
    {
        _connection = connection;
        _state = connection.State;
        _connection.StateChanged += OnStateChanged;
    }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;

    public string StatusText => State switch
    {
        ConnectionState.Connecting => Loc.Get("Status_Connecting"),
        ConnectionState.Connected => Loc.Get("Status_Connected"),
        ConnectionState.Disconnecting => Loc.Get("Status_Disconnecting"),
        ConnectionState.Error => _connection.LastError ?? Loc.Get("Status_Error"),
        _ => Loc.Get("Status_Disconnected"),
    };

    public string ActionText => IsConnected ? Loc.Get("Action_Disconnect") : Loc.Get("Action_Connect");

    // Placeholders until profiles (M1) and routing presets (M2) exist.
    [ObservableProperty]
    private string _serverName = Loc.Get("Server_None");

    [ObservableProperty]
    private string _presetName = Loc.Get("Preset_BypassRu");

    private bool CanToggleConnection() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private Task ToggleConnectionAsync() =>
        IsConnected ? _connection.DisconnectAsync() : _connection.ConnectAsync();

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            State = state;
        }
        else
        {
            Dispatcher.UIThread.Post(() => State = state);
        }
    }

    public void Dispose() => _connection.StateChanged -= OnStateChanged;
}
