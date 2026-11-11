namespace SimpleXray.Core.Abstractions;

/// <param name="ConfigPath">Generated Xray JSON config.</param>
/// <param name="AssetDirectory">Directory with geoip.dat / geosite.dat (XRAY_LOCATION_ASSET).</param>
/// <param name="WorkingDirectory">Working directory of the core process.</param>
public sealed record CoreStartInfo(string ConfigPath, string AssetDirectory, string WorkingDirectory);

public sealed class CoreExitedEventArgs(int exitCode, string recentOutput) : EventArgs
{
    public int ExitCode { get; } = exitCode;

    /// <summary>Last lines written by the core, for error reporting.</summary>
    public string RecentOutput { get; } = recentOutput;
}

/// <summary>Runs the proxy core (xray.exe on Windows, the embedded library on Android).</summary>
public interface ICoreRunner
{
    bool IsRunning { get; }

    /// <summary>Last lines written by the core (stdout + stderr).</summary>
    string RecentOutput { get; }

    /// <summary>Raised when the core exits on its own, i.e. not because of <see cref="StopAsync"/>.</summary>
    event EventHandler<CoreExitedEventArgs>? Exited;

    /// <summary>Starts the core. Returns once the process is started, not when it is ready.</summary>
    /// <exception cref="FileNotFoundException">The core binary is missing.</exception>
    Task StartAsync(CoreStartInfo startInfo, CancellationToken cancellationToken = default);

    /// <summary>Stops the core if it is running. Never throws.</summary>
    Task StopAsync();
}
