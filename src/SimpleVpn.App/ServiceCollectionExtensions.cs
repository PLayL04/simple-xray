using Microsoft.Extensions.DependencyInjection;
using SimpleVpn.App.Services;
using SimpleVpn.App.ViewModels;

namespace SimpleVpn.App;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers platform-independent view models and UI services.</summary>
    public static IServiceCollection AddSimpleVpnApp(this IServiceCollection services)
    {
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IFileService, AvaloniaFileService>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainViewModel>();
        return services;
    }
}
