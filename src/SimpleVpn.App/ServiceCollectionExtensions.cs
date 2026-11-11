using Microsoft.Extensions.DependencyInjection;
using SimpleVpn.App.ViewModels;

namespace SimpleVpn.App;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers platform-independent view models.</summary>
    public static IServiceCollection AddSimpleVpnApp(this IServiceCollection services)
    {
        services.AddTransient<MainViewModel>();
        return services;
    }
}
