using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Qr;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.ViewModels;

/// <summary>"Servers" section of the settings: rename, show as QR / copy the link to pass it on, delete.</summary>
public sealed partial class ServerListViewModel : ViewModelBase, IDisposable
{
    private readonly ProfileRepository _profiles;
    private readonly IConnectionManager _connection;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _ui;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand), nameof(ShowQrCommand), nameof(CopyLinkCommand), nameof(DeleteCommand))]
    private ServerProfile? _selected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>QR of the selected server's link; the QR dialog is open while it is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQrOpen))]
    private QrMatrix? _qr;

    [ObservableProperty]
    private string? _qrTitle;

    [ObservableProperty]
    private string? _message;

    public ServerListViewModel(ProfileRepository profiles, IConnectionManager connection, IClipboardService clipboard, IUiDispatcher ui)
    {
        _profiles = profiles;
        _connection = connection;
        _clipboard = clipboard;
        _ui = ui;
        _profiles.Changed += OnProfilesChanged;
        Reload();
    }

    public ObservableCollection<ServerProfile> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    public bool HasSelection => Selected is not null;

    public bool IsQrOpen => Qr is not null;

    private bool CanAct() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void StartRename()
    {
        ResetPanels();
        RenameText = Selected!.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void SaveRename()
    {
        if (Selected is { } server)
        {
            var name = RenameText.Trim();
            _profiles.Rename(server.Id, name.Length > 0 ? name : server.Address);
        }

        IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary>The link holds the access key; it is shown only on request, never logged.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ShowQr()
    {
        ResetPanels();
        QrTitle = Selected!.DisplayName;
        Qr = QrCodec.Encode(VlessLinkParser.ToLink(Selected));
    }

    [RelayCommand]
    private void CloseQr() => Qr = null;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CopyLinkAsync()
    {
        ResetPanels();
        await _clipboard.SetTextAsync(VlessLinkParser.ToLink(Selected!));
        Message = Loc.Get("Servers_LinkCopied");
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Delete()
    {
        ResetPanels();
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;
        if (Selected is not { } server)
        {
            return;
        }

        // Deleting the server in use: disconnect first so nothing keeps running with a removed profile.
        if (server.Id == _profiles.Selected?.Id && _connection.State is not ConnectionState.Disconnected)
        {
            await _connection.DisconnectAsync();
        }

        _profiles.Remove(server.Id);
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>Called when the settings screen opens.</summary>
    public void Refresh()
    {
        ResetPanels();
        Reload();
    }

    private void ResetPanels()
    {
        IsRenaming = false;
        IsConfirmingDelete = false;
        Qr = null;
        Message = null;
    }

    private void Reload()
    {
        var selectedId = Selected?.Id ?? _profiles.Selected?.Id;
        Servers.Clear();
        foreach (var profile in _profiles.Profiles)
        {
            Servers.Add(profile);
        }

        Selected = Servers.FirstOrDefault(p => p.Id == selectedId) ?? Servers.FirstOrDefault();
        OnPropertyChanged(nameof(HasServers));
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => _ui.Post(Reload);

    public void Dispose() => _profiles.Changed -= OnProfilesChanged;
}
