using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleVpn.App;
using SimpleVpn.App.Localization;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Geo;
using SimpleVpn.Core.Net;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;
using SimpleVpn.Desktop.Platform;

namespace SimpleVpn.Desktop;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything touches the log, state.json or the system proxy.
        using var instance = SingleInstance.Claim(ActivateMainWindow);
        if (!instance.IsFirst)
        {
            return 0;
        }

        using var services = ConfigureServices();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
        var portable = services.GetRequiredService<AppPaths>().IsPortable;
        LogStarting(logger, version, portable);

        var settings = services.GetRequiredService<SettingsStore>().Settings;
        Loc.SetCulture(ParseCulture(args) ?? ParseCulture(settings.Language) ?? CultureInfo.CurrentUICulture);

        var connection = services.GetRequiredService<ConnectionManager>();
        connection.RecoverAfterCrash();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash(logger, e.ExceptionObject as Exception);

            // Best effort: do not leave the system proxy pointing to a dead core.
            connection.DisconnectAsync().Wait(TimeSpan.FromSeconds(3));
        };

        // Background geo auto-update (first check ~30 s after start).
        services.GetRequiredService<GeoUpdateScheduler>().Start();

        var builder = BuildAvaloniaApp(services);

        // Escape hatch for broken GPU drivers / remote sessions.
        if (args.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase))
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }

        var exitCode = builder.StartWithClassicDesktopLifetime(args);
        LogExiting(logger, exitCode);
        return exitCode;
    }

    /// <summary>Another start of the app asked us to show the window.</summary>
    private static void ActivateMainWindow() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }
    });

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
        services.AddLogging(logging => logging
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(paths.LogsDirectory)));

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ProfileRepository>();
        services.AddSingleton<PresetRepository>();

        services.AddSingleton<ICoreRunner>(sp =>
            new XrayProcessRunner(Path.Combine(paths.CoreDirectory, "xray.exe"), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ISystemProxy, WinInetSystemProxy>();
        services.AddSingleton<INetworkProbe, LoopbackNetworkProbe>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IConnectionManager>(sp => sp.GetRequiredService<ConnectionManager>());
        services.AddSingleton<HttpDownloader>();
        services.AddSingleton<GeoUpdater>();
        services.AddSingleton<GeoUpdateScheduler>();

        services.AddSimpleVpnApp();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Supports <c>--lang en</c> / <c>--lang=ru</c> to override the UI language.</summary>
    private static CultureInfo? ParseCulture(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCulture(args[i]["--lang=".Length..]);
            }

            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return ParseCulture(args[i + 1]);
            }
        }

        return null;
    }

    private static CultureInfo? ParseCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SimpleVpn {Version} starting (portable: {Portable})")]
    private static partial void LogStarting(ILogger logger, string version, bool portable);

    [LoggerMessage(Level = LogLevel.Information, Message = "SimpleVpn exiting with code {ExitCode}")]
    private static partial void LogExiting(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception")]
    private static partial void LogCrash(ILogger logger, Exception? exception);
}
