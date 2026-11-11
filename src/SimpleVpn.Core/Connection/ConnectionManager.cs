using Microsoft.Extensions.Logging;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Storage;
using SimpleVpn.Core.Xray;

namespace SimpleVpn.Core.Connection;

/// <summary>
/// System-proxy connection (M1): generate config → start core → wait until it listens → apply system proxy.
/// Every failure rolls back what was already applied. The previous system proxy is persisted in
/// state.json before it is changed, so <see cref="RecoverAfterCrash"/> can restore it after a crash.
/// </summary>
public sealed partial class ConnectionManager : IConnectionManager, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly SettingsStore _settings;
    private readonly ICoreRunner _core;
    private readonly ISystemProxy _systemProxy;
    private readonly INetworkProbe _network;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Set while we stop the core ourselves, so its exit is not reported as a failure.
    private volatile bool _stopping;

    public ConnectionManager(
        AppPaths paths,
        ProfileRepository profiles,
        SettingsStore settings,
        ICoreRunner core,
        ISystemProxy systemProxy,
        INetworkProbe network,
        ILogger<ConnectionManager> logger)
    {
        _paths = paths;
        _profiles = profiles;
        _settings = settings;
        _core = core;
        _systemProxy = systemProxy;
        _network = network;
        _logger = logger;
        _core.Exited += OnCoreExited;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public ConnectionError? LastError { get; private set; }

    /// <summary>Local ports of the running core (valid while connected).</summary>
    public (int Socks, int Http)? ActivePorts { get; private set; }

    /// <summary>How long to wait for the core to start listening.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Call once at startup: restores the user's proxy if the app crashed while connected.</summary>
    public void RecoverAfterCrash()
    {
        if (_settings.LoadState().ProxyApplied)
        {
            LogRecovering(_logger);
            RestoreSystemProxy();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting, error: null);

            // Off the caller's (UI) thread: copying geo files and starting the process can block for
            // seconds (first start is often delayed by antivirus scanning xray.exe).
            await Task.Run(() => ConnectCoreAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Connected, error: null);
        }
        catch (ConnectionFailedException ex)
        {
            LogConnectFailed(_logger, ex.Error.Code, ex.Error.Detail);
            await RollbackAsync().ConfigureAwait(false);
            SetState(ConnectionState.Error, ex.Error);
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync().ConfigureAwait(false);
            SetState(ConnectionState.Disconnected, error: null);
        }
#pragma warning disable CA1031 // Any failure must end in a consistent state, never crash the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpected(_logger, ex);
            await RollbackAsync().ConfigureAwait(false);
            SetState(ConnectionState.Error, new ConnectionError(ConnectionErrorCode.Unexpected, ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            return;
        }

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _core.Exited -= OnCoreExited;
        _gate.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var profile = _profiles.Selected ?? throw Fail(ConnectionErrorCode.NoServer);

        if (!GeoFiles.EnsureAvailable(_paths))
        {
            throw Fail(ConnectionErrorCode.GeoFilesMissing, _paths.GeoDirectory);
        }

        var settings = _settings.Settings;
        var socksPort = _network.AllocatePort(settings.SocksPort, []);
        var httpPort = _network.AllocatePort(settings.HttpPort, [socksPort]);

        Directory.CreateDirectory(_paths.RuntimeDirectory);
        var configPath = Path.Combine(_paths.RuntimeDirectory, "config.json");
        var json = XrayConfigBuilder.BuildJson(new XrayConfigRequest { Profile = profile, SocksPort = socksPort, HttpPort = httpPort });
        await File.WriteAllTextAsync(configPath, json, cancellationToken).ConfigureAwait(false);

        LogStarting(_logger, profile.Network, profile.Security, socksPort, httpPort);
        _stopping = false;
        try
        {
            await _core.StartAsync(new CoreStartInfo(configPath, _paths.GeoDirectory, _paths.RuntimeDirectory), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw Fail(ConnectionErrorCode.CoreNotFound, ex.FileName ?? ex.Message);
        }

        var ready = await _network.WaitForTcpAsync(socksPort, StartTimeout, () => !_core.IsRunning, cancellationToken)
            .ConfigureAwait(false);
        if (!ready)
        {
            var code = _core.IsRunning ? ConnectionErrorCode.CoreStartTimeout : ConnectionErrorCode.CoreExited;
            throw Fail(code, _core.RecentOutput);
        }

        ApplySystemProxy(httpPort);
        ActivePorts = (socksPort, httpPort);
    }

    private void ApplySystemProxy(int httpPort)
    {
        try
        {
            var previous = _systemProxy.GetCurrent();

            // Persist before changing anything: if we crash after Apply, the next start restores it.
            _settings.SaveState(new RuntimeState { ProxyApplied = true, PreviousProxy = previous });
            _systemProxy.Apply(new SystemProxySettings
            {
                Enabled = true,
                Server = $"{XrayConfigBuilder.Loopback}:{httpPort}",
                Bypass = SystemProxySettings.DefaultBypass,
            });
        }
#pragma warning disable CA1031 // Mapped to a user-facing error; rollback restores the previous proxy.
        catch (Exception ex) when (ex is not ConnectionFailedException)
#pragma warning restore CA1031
        {
            throw Fail(ConnectionErrorCode.SystemProxyFailed, ex.Message);
        }
    }

    private async Task DisconnectLockedAsync()
    {
        if (State is ConnectionState.Disconnected)
        {
            return;
        }

        SetState(ConnectionState.Disconnecting, error: null);
        await RollbackAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, error: null);
    }

    /// <summary>Restores the system proxy and stops the core. Never throws.</summary>
    private async Task RollbackAsync()
    {
        RestoreSystemProxy();
        _stopping = true;
        await _core.StopAsync().ConfigureAwait(false);
        ActivePorts = null;
    }

    private void RestoreSystemProxy()
    {
        try
        {
            var state = _settings.LoadState();
            if (!state.ProxyApplied)
            {
                return;
            }

            _systemProxy.Apply(state.PreviousProxy ?? new SystemProxySettings());
            _settings.SaveState(new RuntimeState());
        }
#pragma warning disable CA1031 // Best effort; state.json stays so the next start retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRestoreFailed(_logger, ex);
        }
    }

    private void OnCoreExited(object? sender, CoreExitedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        // The core died while connected: never leave the system proxy pointing to a dead port.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State != ConnectionState.Connected || _stopping)
                {
                    return;
                }

                LogCoreExited(_logger, e.ExitCode, e.RecentOutput);
                await RollbackAsync().ConfigureAwait(false);
                SetState(ConnectionState.Error, new ConnectionError(ConnectionErrorCode.CoreExited, e.RecentOutput));
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void SetState(ConnectionState state, ConnectionError? error)
    {
        LastError = error;
        if (State == state && error is null)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static ConnectionFailedException Fail(ConnectionErrorCode code, string? detail = null) =>
        new(new ConnectionError(code, detail));

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting core: {Network}/{Security}, socks {SocksPort}, http {HttpPort}")]
    private static partial void LogStarting(ILogger logger, string network, string security, int socksPort, int httpPort);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed: {Code}. {Detail}")]
    private static partial void LogConnectFailed(ILogger logger, ConnectionErrorCode code, string? detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while connecting")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core exited unexpectedly with code {ExitCode}. {Output}")]
    private static partial void LogCoreExited(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Previous session did not end cleanly; restoring system proxy")]
    private static partial void LogRecovering(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to restore system proxy")]
    private static partial void LogRestoreFailed(ILogger logger, Exception exception);
}
