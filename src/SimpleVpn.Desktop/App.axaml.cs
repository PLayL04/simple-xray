using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SimpleVpn.App.Services;
using SimpleVpn.App.ViewModels;
using SimpleVpn.Core.Connection;
using SimpleVpn.Desktop.Views;

namespace SimpleVpn.Desktop;

/// <summary>
/// Lives in the tray: closing the window only hides it; the app exits from the tray menu or when the
/// Windows session ends. Either way the connection is torn down (system proxy restored, core stopped).
/// </summary>
#pragma warning disable CA1001 // The tray icon is disposed on Exit; Application itself is never disposed.
public sealed partial class App : Application
#pragma warning restore CA1001
{
    private readonly IServiceProvider _services;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayController? _tray;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var viewModel = _services.GetRequiredService<MainViewModel>();
            _viewModel = viewModel;
            viewModel.ShowRequested += (_, _) => ShowMainWindow();
            viewModel.HideRequested += (_, _) => _window?.Hide();
            _tray = new TrayController(this, viewModel, ShowMainWindow);

            if (!_services.GetRequiredService<StartupOptions>().StartHidden)
            {
                ShowMainWindow();
            }

            Dispatcher.UIThread.Post(async () => await viewModel.OnStartedAsync());

            // Windows session end (WM_QUERYENDSESSION): the process may be killed right after this,
            // so restore the system proxy here rather than in Exit.
            desktop.ShutdownRequested += (_, _) => Disconnect();
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                Disconnect();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shows (creating on first use) and activates the main window.</summary>
    public void ShowMainWindow()
    {
        if (_desktop is null || _viewModel is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += OnWindowClosing;
            _desktop.MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>The user closed the window (X, Alt+F4): keep running in the tray.</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            _viewModel?.OnCloseRequested();
        }
    }

    private void Disconnect() =>
        _services.GetRequiredService<IConnectionManager>().DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
}
