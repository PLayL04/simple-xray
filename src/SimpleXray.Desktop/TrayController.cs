using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using SimpleXray.App.Localization;
using SimpleXray.App.ViewModels;
using SimpleXray.Core.Connection;

namespace SimpleXray.Desktop;

/// <summary>
/// Notification area icon: the colour shows the connection state; the menu has open, connect/disconnect,
/// server and routing pickers and exit. Everything goes through <see cref="MainViewModel"/>.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _toggle;
    private readonly NativeMenuItem _servers;
    private readonly NativeMenuItem _presets;
    private readonly Dictionary<ConnectionState, WindowIcon> _icons;

    public TrayController(Application app, MainViewModel viewModel, Action showWindow)
    {
        _viewModel = viewModel;
        var off = LoadIcon("tray-off");
        var busy = LoadIcon("tray-busy");
        _icons = new()
        {
            [ConnectionState.Disconnected] = off,
            [ConnectionState.Connecting] = busy,
            [ConnectionState.Disconnecting] = busy,
            [ConnectionState.Connected] = LoadIcon("tray-on"),
            [ConnectionState.Error] = LoadIcon("tray-error"),
        };

        var open = new NativeMenuItem(Loc.Get("Tray_Open"));
        open.Click += (_, _) => showWindow();
        _toggle = new NativeMenuItem { Command = viewModel.ToggleConnectionCommand };
        _servers = new NativeMenuItem(Loc.Get("Tray_Server")) { Menu = new NativeMenu() };
        _presets = new NativeMenuItem(Loc.Get("Tray_Routing")) { Menu = new NativeMenu() };
        var exit = new NativeMenuItem(Loc.Get("Tray_Exit")) { Command = viewModel.ExitCommand };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_toggle);
        menu.Items.Add(_servers);
        menu.Items.Add(_presets);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon { Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(app, [_tray]);

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Servers.CollectionChanged += OnServersChanged;
        _viewModel.Presets.CollectionChanged += OnPresetsChanged;
        RefreshState();
        RefreshServers();
        RefreshPresets();
    }

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://SimpleXray/Assets/{name}.ico")));

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.State) or nameof(MainViewModel.StatusText):
                RefreshState();
                break;
            case nameof(MainViewModel.SelectedServer):
                RefreshServers();
                break;
            case nameof(MainViewModel.SelectedPreset):
                RefreshPresets();
                break;
        }
    }

    private void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshServers();

    private void OnPresetsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPresets();

    private void RefreshState()
    {
        _tray.Icon = _icons[_viewModel.State];
        _tray.ToolTipText = _viewModel.TrayToolTip;
        _toggle.Header = _viewModel.ActionText;
    }

    private void RefreshServers()
    {
        var items = _servers.Menu!.Items;
        items.Clear();
        foreach (var server in _viewModel.Servers)
        {
            var item = new NativeMenuItem(server.DisplayName)
            {
                Icon = ServerFlags.For(server.Country),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = server.Id == _viewModel.SelectedServer?.Id,
            };
            item.Click += (_, _) => _viewModel.SelectedServer = server;
            items.Add(item);
        }

        _servers.IsVisible = items.Count > 0;
    }

    private void RefreshPresets()
    {
        var items = _presets.Menu!.Items;
        items.Clear();
        foreach (var preset in _viewModel.Presets)
        {
            var item = new NativeMenuItem(preset.Name)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = preset.Id == _viewModel.SelectedPreset?.Id,
            };
            item.Click += (_, _) => _viewModel.SelectedPreset = preset;
            items.Add(item);
        }
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Servers.CollectionChanged -= OnServersChanged;
        _viewModel.Presets.CollectionChanged -= OnPresetsChanged;
        _tray.Dispose();
    }
}
