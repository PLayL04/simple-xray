using System.Net.Sockets;
using System.Net;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SimpleXray.Core.Abstractions;
using SimpleXray.Core.Models;
using SimpleXray.Core.Routing;
using SimpleXray.Core.Storage;
using SimpleXray.Core.Xray;

namespace SimpleXray.Core.Connection;

/// <summary>
/// Connection in system-proxy or TUN mode: generate config → start core → wait until it listens → apply system proxy.
/// Every failure rolls back what was already applied. The previous system proxy is persisted in
/// state.json before it is changed, so <see cref="RecoverAfterCrash"/> can restore it after a crash.
/// </summary>
public sealed partial class ConnectionManager : IConnectionManager, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly PresetRepository _presets;
    private readonly SettingsStore _settings;
    private readonly ICoreRunner _core;
    private readonly ISystemProxy _systemProxy;
    private readonly INetworkProbe _network;
    private readonly IElevation _elevation;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Set while we stop the core ourselves, so its exit is not reported as a failure.
    private volatile bool _stopping;

    public ConnectionManager(
        AppPaths paths,
        ProfileRepository profiles,
        PresetRepository presets,
        SettingsStore settings,
        ICoreRunner core,
        ISystemProxy systemProxy,
        INetworkProbe network,
        IElevation elevation,
        ILogger<ConnectionManager> logger)
    {
        _paths = paths;
        _profiles = profiles;
        _presets = presets;
        _settings = settings;
        _core = core;
        _systemProxy = systemProxy;
        _network = network;
        _elevation = elevation;
        _logger = logger;
        _core.Exited += OnCoreExited;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public ConnectionError? LastError { get; private set; }

    /// <summary>Local ports of the running core (valid while connected).</summary>
    public (int Socks, int Http)? ActivePorts { get; private set; }

    /// <summary>Mode of the running connection (valid while connected).</summary>
    public ConnectionMode? ActiveMode { get; private set; }

    /// <summary>How long to check whether another local proxy (e.g. v2rayN) still listens before restoring it.</summary>
    private static readonly TimeSpan LocalProxyProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the core to start listening.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Call once at startup: restores the user's proxy if the app crashed while connected.</summary>
    public void RecoverAfterCrash()
    {
        if (_settings.LoadState().ProxyApplied)
        {
            LogRecovering(_logger);
            RestoreSystemProxyAsync().GetAwaiter().GetResult();
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

    /// <summary>
    /// Applies a new server, preset or settings while connected: restarts the core with a fresh config and keeps
    /// the system proxy in place (the UI shows "Connecting", never "Disconnected"). Does nothing when not connected.
    /// </summary>
    public async Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting, error: null);
            LogRestarting(_logger);
            await Task.Run(
                async () =>
                {
                    if (_settings.Settings.Mode != ActiveMode)
                    {
                        // Proxy <-> TUN: undo everything of the old mode (system proxy) and start from scratch.
                        await RollbackAsync().ConfigureAwait(false);
                        await StartCoreAsync(previousPorts: null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    _stopping = true;
                    await _core.StopAsync().ConfigureAwait(false);
                    await StartCoreAsync(ActivePorts, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
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

    public void Dispose()
    {
        _core.Exited -= OnCoreExited;
        _gate.Dispose();
    }

    private Task ConnectCoreAsync(CancellationToken cancellationToken) =>
        StartCoreAsync(previousPorts: null, cancellationToken);

    /// <summary>
    /// Writes the config, starts the core, waits until it listens and points the system proxy at it.
    /// On a restart the previous ports are preferred so the already applied system proxy stays valid.
    /// </summary>
    private async Task StartCoreAsync((int Socks, int Http)? previousPorts, CancellationToken cancellationToken)
    {
        var profile = _profiles.Selected ?? throw Fail(ConnectionErrorCode.NoServer);

        if (!GeoFiles.EnsureAvailable(_paths))
        {
            throw Fail(ConnectionErrorCode.GeoFilesMissing, _paths.GeoDirectory);
        }

        var settings = _settings.Settings;
        var mode = settings.Mode;
        if (mode == ConnectionMode.Tun && !_elevation.IsElevated)
        {
            throw Fail(ConnectionErrorCode.ElevationRequired);
        }

        var preset = _presets.Selected;
        var socksPort = _network.AllocatePort(previousPorts?.Socks ?? settings.SocksPort, []);
        var httpPort = _network.AllocatePort(previousPorts?.Http ?? settings.HttpPort, [socksPort]);

        Directory.CreateDirectory(_paths.RuntimeDirectory);
        var configPath = Path.Combine(_paths.RuntimeDirectory, "config.json");
        var json = XrayConfigBuilder.BuildJson(new XrayConfigRequest
        {
            Profile = profile,
            SocksPort = socksPort,
            HttpPort = httpPort,
            Preset = preset,
            Dns = settings.Dns,
            Mode = mode,
            Tun = settings.Tun,
            LogLevel = settings.CoreLogLevel,
        });
        await File.WriteAllTextAsync(configPath, json, cancellationToken).ConfigureAwait(false);

        LogStarting(_logger, profile.Network, profile.Security, preset.Id, mode, socksPort, httpPort);
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

        if (mode == ConnectionMode.Tun)
        {
            // The TUN inbound starts before SOCKS, but make sure the adapter is really up before reporting success.
            var tunUp = await _network.WaitForInterfaceAsync(XrayConfigBuilder.TunName, StartTimeout, () => !_core.IsRunning, cancellationToken)
                .ConfigureAwait(false);
            if (!tunUp)
            {
                throw Fail(_core.IsRunning ? ConnectionErrorCode.TunFailed : ConnectionErrorCode.CoreExited, _core.RecentOutput);
            }
        }
        else if (previousPorts is null || previousPorts.Value.Http != httpPort)
        {
            ApplySystemProxy(httpPort);
        }

        ActivePorts = (socksPort, httpPort);
        ActiveMode = mode;
    }

    private void ApplySystemProxy(int httpPort)
    {
        try
        {
            // Persist the user's own proxy before changing anything: if we crash after Apply, the next start
            // restores it. When our proxy is already applied (restart on another port) keep the saved original.
            if (!_settings.LoadState().ProxyApplied)
            {
                var previous = _systemProxy.GetCurrent();
                LogSavedProxy(_logger, previous.Enabled, previous.Server, previous.AutoConfigUrl is not null, previous.AutoDetect);
                _settings.SaveState(new RuntimeState { ProxyApplied = true, PreviousProxy = previous });
            }

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
        await RestoreSystemProxyAsync().ConfigureAwait(false);
        _stopping = true;
        await _core.StopAsync().ConfigureAwait(false);
        ActivePorts = null;
        ActiveMode = null;
    }

    private async Task RestoreSystemProxyAsync()
    {
        try
        {
            var state = _settings.LoadState();
            if (!state.ProxyApplied)
            {
                return;
            }

            var previous = state.PreviousProxy ?? new SystemProxySettings();
            if (previous.Enabled && await IsDeadLocalProxyAsync(previous.Server).ConfigureAwait(false))
            {
                // E.g. v2rayN's 127.0.0.1:10808 was on before we connected and v2rayN has been closed since:
                // turning it back on would leave the user without internet. Keep the address, switch it off.
                LogDeadProxySkipped(_logger, previous.Server);
                previous = previous with { Enabled = false };
            }

            _systemProxy.Apply(previous);
            _settings.SaveState(new RuntimeState());
            LogRestoredProxy(_logger, previous.Enabled, previous.Server);
        }
#pragma warning disable CA1031 // Best effort; state.json stays so the next start retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRestoreFailed(_logger, ex);
        }
    }

    /// <summary>True when the proxy points only at this machine and nothing listens there (or only our own core does).</summary>
    private async Task<bool> IsDeadLocalProxyAsync(string? server)
    {
        var ports = LocalProxyPorts(server);
        if (ports is null)
        {
            return false;
        }

        foreach (var port in ports)
        {
            var ours = ActivePorts is { } active && (port == active.Http || port == active.Socks);
            if (!ours && await _network.WaitForTcpAsync(port, LocalProxyProbeTimeout, () => false).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ports of a WinINet proxy string that points only at this machine ("127.0.0.1:10808",
    /// "http=localhost:1080;https=127.0.0.1:1080"); null when any part is remote or cannot be parsed.
    /// </summary>
    private static List<int>? LocalProxyPorts(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var ports = new List<int>();
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var value = equals >= 0 ? part[(equals + 1)..] : part;
            var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                value = value[(schemeEnd + 3)..];
            }

            var colon = value.LastIndexOf(':');
            if (colon <= 0
                || !int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                return null;
            }

            var host = value[..colon];
            var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(ip));
            if (!loopback)
            {
                return null;
            }

            ports.Add(port);
        }

        return ports.Count > 0 ? ports : null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting core: {Network}/{Security}, preset {Preset}, mode {Mode}, socks {SocksPort}, http {HttpPort}")]
    private static partial void LogStarting(ILogger logger, string network, string security, string preset, ConnectionMode mode, int socksPort, int httpPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarting core to apply new settings")]
    private static partial void LogRestarting(ILogger logger);

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

    // Proxy servers are local or corporate addresses, not secrets; logged so "my proxy stayed on" reports can be explained.
    [LoggerMessage(Level = LogLevel.Information, Message = "Saved the user's system proxy: enabled {Enabled}, server {Server}, PAC {Pac}, auto-detect {AutoDetect}")]
    private static partial void LogSavedProxy(ILogger logger, bool enabled, string? server, bool pac, bool autoDetect);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The previous system proxy {Server} points to this machine but nothing listens there; restoring it switched off")]
    private static partial void LogDeadProxySkipped(ILogger logger, string? server);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored the user's system proxy: enabled {Enabled}, server {Server}")]
    private static partial void LogRestoredProxy(ILogger logger, bool enabled, string? server);
}
