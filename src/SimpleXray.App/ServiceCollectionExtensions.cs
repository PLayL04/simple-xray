using Microsoft.Extensions.DependencyInjection;
using SimpleXray.App.Services;
using SimpleXray.App.ViewModels;

namespace SimpleXray.App;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers platform-independent view models and UI services.</summary>
    public static IServiceCollection AddSimpleXrayApp(this IServiceCollection services)
    {
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<IFileService, AvaloniaFileService>();
        services.AddSingleton<IAppLifetime, AvaloniaAppLifetime>();
        services.AddTransient<GeoSettingsViewModel>();
        services.AddTransient<ServerListViewModel>();
        services.AddTransient<LogViewerViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainViewModel>();
        return services;
    }
}
