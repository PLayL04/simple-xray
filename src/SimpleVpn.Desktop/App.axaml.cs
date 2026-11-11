using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SimpleVpn.App.ViewModels;
using SimpleVpn.Core.Connection;
using SimpleVpn.Desktop.Views;

namespace SimpleVpn.Desktop;

public sealed partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };

            // Closing the app disconnects: restores the system proxy and stops the core.
            // TODO(M5): with the tray icon, closing the window will only hide it.
            desktop.Exit += (_, _) =>
                _services.GetRequiredService<IConnectionManager>().DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
