using System.Globalization;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SimpleVpn.App;
using SimpleVpn.App.Localization;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Storage;
using SimpleVpn.Desktop.Platform;

namespace SimpleVpn.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Loc.SetCulture(ParseCulture(args) ?? CultureInfo.CurrentUICulture);

        using var services = ConfigureServices();
        var builder = BuildAvaloniaApp(services);

        // Escape hatch for broken GPU drivers / remote sessions.
        if (args.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase))
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }

        return builder.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Entry point for the Avalonia XAML previewer. Do not remove.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(ConfigureServices());

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static ServiceProvider ConfigureServices()
    {
        var paths = WindowsAppPaths.Resolve();
        paths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(paths);

        // TODO(M1): replace with the real ConnectionManager (Xray process + system proxy).
        services.AddSingleton<IConnectionManager, StubConnectionManager>();

        services.AddSimpleVpnApp();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Supports <c>--lang en</c> / <c>--lang=ru</c> to override the UI language.</summary>
    private static CultureInfo? ParseCulture(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i].StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                value = args[i]["--lang=".Length..];
            }
            else if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    return CultureInfo.GetCultureInfo(value);
                }
                catch (CultureNotFoundException)
                {
                    return null;
                }
            }
        }

        return null;
    }
}
