using Microsoft.Extensions.Logging.Abstractions;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Connection;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.Core.Tests.Connection;

public sealed class ConnectionManagerTests : IDisposable
{
    private static readonly SystemProxySettings UserProxy = new() { Enabled = true, Server = "corp-proxy:3128", Bypass = "<local>" };

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplevpn-cm-");
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly SettingsStore _settings;
    private readonly PresetRepository _presets;
    private readonly FakeCore _core = new();
    private readonly FakeSystemProxy _proxy = new() { Current = UserProxy };
    private readonly FakeNetwork _network = new();
    private readonly FakeElevation _elevation = new();

    public ConnectionManagerTests()
    {
        _paths = AppPaths.Resolve(_root.FullName, Path.Combine(_root.FullName, "roaming"), Path.Combine(_root.FullName, "local"), _ => false);
        _paths.EnsureCreated();
        Directory.CreateDirectory(_paths.SeedGeoDirectory);
        foreach (var name in GeoFiles.Required)
        {
            File.WriteAllText(Path.Combine(_paths.SeedGeoDirectory, name), "seed");
        }

        _profiles = new ProfileRepository(_paths);
        _settings = new SettingsStore(_paths);
        _presets = new PresetRepository(_paths, _settings, NullLogger<PresetRepository>.Instance);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private ConnectionManager CreateManager() =>
        new(_paths, _profiles, _presets, _settings, _core, _proxy, _network, _elevation, NullLogger<ConnectionManager>.Instance)
        {
            StartTimeout = TimeSpan.FromMilliseconds(200),
        };

    private void AddServer() => _profiles.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls));

    [Fact]
    public async Task Connect_Success_StartsCoreAndAppliesSystemProxy()
    {
        AddServer();
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.True(_core.IsRunning);
        Assert.True(_proxy.Current.Enabled);
        Assert.Equal("127.0.0.1:10889", _proxy.Current.Server);
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeDirectory, "config.json")));
        Assert.True(File.Exists(Path.Combine(_paths.GeoDirectory, "geoip.dat")), "geo files copied from seed");

        var state = _settings.LoadState();
        Assert.True(state.ProxyApplied);
        Assert.Equal(UserProxy, state.PreviousProxy);
    }

    [Fact]
    public async Task Disconnect_RestoresPreviousProxyAndStopsCore()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        await manager.DisconnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.False(_core.IsRunning);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_settings.LoadState().ProxyApplied);
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public async Task Connect_WithoutServer_ReportsNoServer()
    {
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Error, manager.State);
        Assert.Equal(ConnectionErrorCode.NoServer, manager.LastError?.Code);
        Assert.Equal(0, _core.StartCalls);
    }

    [Fact]
    public async Task Connect_CoreBinaryMissing_ReportsCoreNotFound()
    {
        AddServer();
        _core.ThrowOnStart = new FileNotFoundException("missing", "xray.exe");
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.CoreNotFound, manager.LastError?.Code);
        Assert.Equal(UserProxy, _proxy.Current);
    }

    [Fact]
    public async Task Connect_GeoFilesMissing_ReportsError()
    {
        AddServer();
        File.Delete(Path.Combine(_paths.SeedGeoDirectory, "geosite.dat"));
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.GeoFilesMissing, manager.LastError?.Code);
    }

    [Fact]
    public async Task Connect_CoreExitsDuringStart_ReportsOutputAndKeepsUserProxy()
    {
        AddServer();
        _network.Ready = false;
        _core.ExitImmediately = true;
        _core.RecentOutput = "Failed to start: bad config";
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Error, manager.State);
        Assert.Equal(ConnectionErrorCode.CoreExited, manager.LastError?.Code);
        Assert.Contains("bad config", manager.LastError?.Detail, StringComparison.Ordinal);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.Equal(0, _proxy.ApplyCalls);
    }

    [Fact]
    public async Task Connect_CoreNeverListens_TimesOutAndStopsCore()
    {
        AddServer();
        _network.Ready = false;
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.CoreStartTimeout, manager.LastError?.Code);
        Assert.False(_core.IsRunning);
    }

    [Fact]
    public async Task Connect_SystemProxyFails_RollsBackEverything()
    {
        AddServer();
        _proxy.FailNextApply = true;
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.SystemProxyFailed, manager.LastError?.Code);
        Assert.False(_core.IsRunning);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_settings.LoadState().ProxyApplied);
    }

    [Fact]
    public async Task Connect_PreferredPortsBusy_UsesAllocatedPorts()
    {
        AddServer();
        _network.PortOverride = p => p + 1000;
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal((11888, 11889), manager.ActivePorts);
        Assert.Equal("127.0.0.1:11889", _proxy.Current.Server);
    }

    [Fact]
    public async Task CoreCrashWhileConnected_RestoresProxyAndReportsError()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);
        var errorRaised = new TaskCompletionSource();
        manager.StateChanged += (_, s) =>
        {
            if (s == ConnectionState.Error)
            {
                errorRaised.TrySetResult();
            }
        };

        _core.Crash(exitCode: 2, output: "panic");
        await errorRaised.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.CoreExited, manager.LastError?.Code);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_settings.LoadState().ProxyApplied);
    }

    [Fact]
    public void RecoverAfterCrash_RestoresProxyFromStateFile()
    {
        _settings.SaveState(new RuntimeState { ProxyApplied = true, PreviousProxy = UserProxy });
        _proxy.Current = new SystemProxySettings { Enabled = true, Server = "127.0.0.1:10889" };
        using var manager = CreateManager();

        manager.RecoverAfterCrash();

        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public void RecoverAfterCrash_CleanShutdown_DoesNotTouchProxy()
    {
        using var manager = CreateManager();

        manager.RecoverAfterCrash();

        Assert.Equal(0, _proxy.ApplyCalls);
    }

    [Fact]
    public async Task ReconnectIfConnected_UsesNewlySelectedServer()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        _profiles.AddOrUpdate(VlessLinkParser.Parse(TestLinks.RealityVision));
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(2, _core.StartCalls);
        var config = await File.ReadAllTextAsync(Path.Combine(_paths.RuntimeDirectory, "config.json"), TestContext.Current.CancellationToken);
        Assert.Contains("realitySettings", config, StringComparison.Ordinal);
        Assert.Equal(UserProxy, _settings.LoadState().PreviousProxy);
    }

    [Fact]
    public async Task ReconnectIfConnected_KeepsSystemProxyAndNeverReportsDisconnected()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);
        var states = new List<ConnectionState>();
        manager.StateChanged += (_, s) => states.Add(s);

        _presets.Select(BuiltInPresets.BlockedOnlyId);
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected], states);
        Assert.Equal(1, _proxy.ApplyCalls);
        Assert.Equal("127.0.0.1:10889", _proxy.Current.Server);
        var config = await File.ReadAllTextAsync(Path.Combine(_paths.RuntimeDirectory, "config.json"), TestContext.Current.CancellationToken);
        Assert.Contains("geosite:ru-blocked", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconnectIfConnected_HttpPortTaken_ReappliesProxyButKeepsOriginalSnapshot()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        _network.PortOverride = p => p == 10889 ? 20889 : p;
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("127.0.0.1:20889", _proxy.Current.Server);
        Assert.Equal(UserProxy, _settings.LoadState().PreviousProxy);

        await manager.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UserProxy, _proxy.Current);
    }

    [Fact]
    public async Task ReconnectIfConnected_CoreFailsToRestart_RollsBackToUserProxy()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        _network.Ready = false;
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Error, manager.State);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_core.IsRunning);
        Assert.False(_settings.LoadState().ProxyApplied);
    }

    [Fact]
    public async Task Connect_UsesSelectedPresetAndDnsSettings()
    {
        AddServer();
        _settings.Update(s => s with { SelectedPresetId = BuiltInPresets.BypassRuId, Dns = s.Dns with { PreferIPv4 = false } });
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        var config = await File.ReadAllTextAsync(Path.Combine(_paths.RuntimeDirectory, "config.json"), TestContext.Current.CancellationToken);
        Assert.Contains("geoip:ru", config, StringComparison.Ordinal);
        Assert.Contains("\"UseIP\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tun_NotElevated_ReportsElevationRequiredWithoutStartingCore()
    {
        AddServer();
        _settings.Update(s => s with { Mode = ConnectionMode.Tun });
        _elevation.IsElevated = false;
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.ElevationRequired, manager.LastError?.Code);
        Assert.Equal(0, _core.StartCalls);
        Assert.Equal(UserProxy, _proxy.Current);
    }

    [Fact]
    public async Task Tun_Elevated_StartsTunAndLeavesSystemProxyAlone()
    {
        AddServer();
        _settings.Update(s => s with { Mode = ConnectionMode.Tun });
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(ConnectionMode.Tun, manager.ActiveMode);
        Assert.Equal(0, _proxy.ApplyCalls);
        Assert.False(_settings.LoadState().ProxyApplied);
        var config = await File.ReadAllTextAsync(Path.Combine(_paths.RuntimeDirectory, "config.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"protocol\": \"tun\"", config, StringComparison.Ordinal);

        await manager.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _proxy.ApplyCalls);
        Assert.False(_core.IsRunning);
    }

    [Fact]
    public async Task Tun_AdapterDoesNotComeUp_ReportsTunFailedAndStopsCore()
    {
        AddServer();
        _settings.Update(s => s with { Mode = ConnectionMode.Tun });
        _network.InterfaceUp = false;
        using var manager = CreateManager();

        await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.TunFailed, manager.LastError?.Code);
        Assert.False(_core.IsRunning);
    }

    [Fact]
    public async Task SwitchingProxyToTunWhileConnected_RestoresUserProxyAndStartsTun()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(_proxy.Current.Enabled && _proxy.Current.Server == "127.0.0.1:10889");

        _settings.Update(s => s with { Mode = ConnectionMode.Tun });
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(ConnectionMode.Tun, manager.ActiveMode);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_settings.LoadState().ProxyApplied);
    }

    [Fact]
    public async Task SwitchingToTunWithoutRights_EndsInElevationRequiredWithProxyRestored()
    {
        AddServer();
        using var manager = CreateManager();
        await manager.ConnectAsync(TestContext.Current.CancellationToken);
        _elevation.IsElevated = false;

        _settings.Update(s => s with { Mode = ConnectionMode.Tun });
        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionErrorCode.ElevationRequired, manager.LastError?.Code);
        Assert.Equal(UserProxy, _proxy.Current);
        Assert.False(_core.IsRunning);
    }

    [Fact]
    public async Task ReconnectIfConnected_WhenDisconnected_DoesNothing()
    {
        AddServer();
        using var manager = CreateManager();

        await manager.ReconnectIfConnectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _core.StartCalls);
    }

    private sealed class FakeCore : ICoreRunner
    {
        public bool IsRunning { get; private set; }

        public string RecentOutput { get; set; } = string.Empty;

        public int StartCalls { get; private set; }

        public Exception? ThrowOnStart { get; set; }

        public bool ExitImmediately { get; set; }

        public event EventHandler<CoreExitedEventArgs>? Exited;

        public Task StartAsync(CoreStartInfo startInfo, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (ThrowOnStart is not null)
            {
                throw ThrowOnStart;
            }

            IsRunning = !ExitImmediately;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Crash(int exitCode, string output)
        {
            IsRunning = false;
            Exited?.Invoke(this, new CoreExitedEventArgs(exitCode, output));
        }
    }

    private sealed class FakeSystemProxy : ISystemProxy
    {
        public SystemProxySettings Current { get; set; } = new();

        public int ApplyCalls { get; private set; }

        public bool FailNextApply { get; set; }

        public SystemProxySettings GetCurrent() => Current;

        public void Apply(SystemProxySettings settings)
        {
            ApplyCalls++;
            if (FailNextApply)
            {
                FailNextApply = false;
                throw new UnauthorizedAccessException("registry denied");
            }

            Current = settings;
        }
    }

    private sealed class FakeElevation : IElevation
    {
        public bool IsElevated { get; set; } = true;

        public bool RestartElevated() => false;
    }

    private sealed class FakeNetwork : INetworkProbe
    {
        public bool Ready { get; set; } = true;

        public bool InterfaceUp { get; set; } = true;

        public Task<bool> WaitForInterfaceAsync(string name, TimeSpan timeout, Func<bool> shouldAbort, CancellationToken cancellationToken = default) =>
            Task.FromResult(InterfaceUp && !shouldAbort());

        public Func<int, int> PortOverride { get; set; } = p => p;

        public int AllocatePort(int preferred, IReadOnlyCollection<int> exclude) => PortOverride(preferred);

        public async Task<bool> WaitForTcpAsync(int port, TimeSpan timeout, Func<bool> shouldAbort, CancellationToken cancellationToken = default)
        {
            if (Ready)
            {
                return !shouldAbort();
            }

            if (!shouldAbort())
            {
                await Task.Delay(timeout, cancellationToken);
            }

            return false;
        }
    }
}
