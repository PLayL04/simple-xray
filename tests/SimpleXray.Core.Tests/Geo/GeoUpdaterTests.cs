using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleXray.Core.Connection;
using SimpleXray.Core.Geo;
using SimpleXray.Core.Net;
using SimpleXray.Core.Routing;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Tests.Geo;

public sealed class GeoUpdaterTests : IDisposable
{
    private static readonly string[] GeoipCategories = ["private", "ru", "ru-blocked", "telegram"];
    private static readonly string[] GeositeCategories = ["private", "ru-available-only-inside", "ru-blocked", "category-ru"];

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplexray-geo-");
    private readonly TestHttpServer _server = new();
    private readonly FakeConnection _connection = new();
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly PresetRepository _presets;

    public GeoUpdaterTests()
    {
        _paths = AppPaths.Resolve(_root.FullName, Path.Combine(_root.FullName, "roaming"), Path.Combine(_root.FullName, "local"), _ => false);
        _paths.EnsureCreated();
        Directory.CreateDirectory(_paths.SeedGeoDirectory);
        File.WriteAllBytes(Path.Combine(_paths.SeedGeoDirectory, "geoip.dat"), Dat(GeoipCategories));
        File.WriteAllBytes(Path.Combine(_paths.SeedGeoDirectory, "geosite.dat"), Dat(GeositeCategories));
        _settings = new SettingsStore(_paths);
        _presets = new PresetRepository(_paths, _settings, NullLogger<PresetRepository>.Instance);
    }

    public void Dispose()
    {
        _server.Dispose();
        _root.Delete(recursive: true);
    }

    private GeoUpdater Create() =>
        new(_paths, _settings, _presets, _connection, new HttpDownloader(_connection), NullLogger<GeoUpdater>.Instance)
        {
            DefaultBaseUri = _server.BaseUri,
        };

    /// <summary>A valid geo file: entries with the given category names plus some payload.</summary>
    internal static byte[] Dat(IEnumerable<string> categories, string extra = "")
    {
        using var ms = new MemoryStream();
        foreach (var name in categories)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name.ToUpperInvariant());
            var payload = Encoding.UTF8.GetBytes("payload" + extra);
            var entry = new List<byte> { 0x0A, (byte)nameBytes.Length };
            entry.AddRange(nameBytes);
            entry.Add(0x12);
            entry.Add((byte)payload.Length);
            entry.AddRange(payload);
            ms.WriteByte(0x0A);
            ms.WriteByte((byte)entry.Count);
            ms.Write([.. entry]);
        }

        return ms.ToArray();
    }

    private void Publish(string name, byte[] content, bool withChecksum = true, string? checksumOverride = null)
    {
        _server.Files["/" + name] = content;
        if (withChecksum)
        {
            var hash = checksumOverride ?? Convert.ToHexStringLower(SHA256.HashData(content));
            _server.Files["/" + name + ".sha256sum"] = Encoding.ASCII.GetBytes($"{hash}  {name}\n");
        }
    }

    private byte[] Installed(string name) => File.ReadAllBytes(Path.Combine(_paths.GeoDirectory, name));

    [Fact]
    public async Task SameChecksum_IsUpToDateWithoutDownloading()
    {
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(GeositeCategories));
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.UpToDate, outcome);
        Assert.Equal(0, _server.RequestCount("/geoip.dat"));
        Assert.NotNull(updater.Status.LastCheckUtc);
        Assert.Null(updater.Status.LastUpdateUtc);
    }

    [Fact]
    public async Task NewFile_IsInstalledWithBackupAndStateSaved()
    {
        var newGeosite = Dat(GeositeCategories, extra: "-v2");
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", newGeosite);
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.Updated, outcome);
        Assert.Equal(newGeosite, Installed("geosite.dat"));
        Assert.Equal(Dat(GeositeCategories), File.ReadAllBytes(Path.Combine(_paths.GeoDirectory, "geosite.dat.bak")));
        Assert.False(File.Exists(Path.Combine(_paths.GeoDirectory, "geosite.dat.download")));
        Assert.Equal(0, _server.RequestCount("/geoip.dat"));

        using var reloaded = Create();
        Assert.NotNull(reloaded.Status.LastUpdateUtc);
        Assert.False(reloaded.IsDue(DateTimeOffset.UtcNow));
        Assert.True(reloaded.IsDue(DateTimeOffset.UtcNow.AddHours(25)));
    }

    [Fact]
    public async Task ChecksumMismatch_KeepsOldFiles()
    {
        Publish("geoip.dat", Dat(GeoipCategories, extra: "-v2"), checksumOverride: new string('a', 64));
        Publish("geosite.dat", Dat(GeositeCategories));
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.Failed, outcome);
        Assert.Equal(GeoUpdateError.ChecksumMismatch, updater.Status.LastError);
        Assert.Equal(Dat(GeoipCategories), Installed("geoip.dat"));
        Assert.Null(updater.Status.LastCheckUtc);
        Assert.True(updater.IsDue(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GarbageWithValidChecksum_IsRejectedAsInvalidFile()
    {
        Publish("geoip.dat", Encoding.ASCII.GetBytes("<html>blocked by provider</html>"));
        Publish("geosite.dat", Dat(GeositeCategories));
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.InvalidFile, updater.Status.LastError);
        Assert.Equal(Dat(GeoipCategories), Installed("geoip.dat"));
    }

    [Fact]
    public async Task MissingPresetCategory_IsRejected()
    {
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(["private", "category-ru"]));
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.MissingCategory, updater.Status.LastError);
        Assert.Contains("geosite:ru-blocked", updater.Status.LastErrorDetail, StringComparison.Ordinal);
        Assert.Equal(Dat(GeositeCategories), Installed("geosite.dat"));
    }

    [Fact]
    public async Task ActiveUserPresetCategory_IsAlsoRequired()
    {
        File.WriteAllBytes(Path.Combine(_paths.SeedGeoDirectory, "geosite.dat"), Dat([.. GeositeCategories, "youtube"]));
        var preset = _presets.Import("""{ "name": "x", "rules": [ { "domain": ["geosite:youtube@ads"] } ] }""");
        _presets.Select(preset.Id);
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(GeositeCategories, extra: "-v2"));
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.MissingCategory, updater.Status.LastError);
        Assert.Equal("geosite:youtube", updater.Status.LastErrorDetail);
    }

    [Fact]
    public async Task CategoryMissingAlreadyInCurrentFile_DoesNotBlockUpdate()
    {
        var preset = _presets.Import("""{ "name": "x", "rules": [ { "domain": ["geosite:typo-category"] } ] }""");
        _presets.Select(preset.Id);
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(GeositeCategories, extra: "-v2"));
        using var updater = Create();

        Assert.Equal(GeoUpdateOutcome.Updated, await updater.UpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistentFailure_WaitsForInterval_TransientRetriesSoon()
    {
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(["private"]));
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.MissingCategory, updater.Status.LastError);
        Assert.False(updater.IsDue(DateTimeOffset.UtcNow.AddHours(2)));
        Assert.True(updater.IsDue(DateTimeOffset.UtcNow.AddHours(25)));
    }

    [Fact]
    public async Task UserDisconnectsDuringRestart_IsNotTreatedAsCoreRejection()
    {
        Publish("geoip.dat", Dat(GeoipCategories, extra: "-v2"));
        Publish("geosite.dat", Dat(GeositeCategories));
        _connection.SetState(ConnectionState.Connected);
        _connection.DisconnectInsteadOfReconnect = true;
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.Updated, outcome);
        Assert.Equal(0, _connection.ConnectCalls);
        Assert.Equal(ConnectionState.Disconnected, _connection.State);
    }

    [Fact]
    public async Task MissingChecksumOnDefaultSource_Fails()
    {
        Publish("geoip.dat", Dat(GeoipCategories), withChecksum: false);
        Publish("geosite.dat", Dat(GeositeCategories));
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.DownloadFailed, updater.Status.LastError);
    }

    [Fact]
    public async Task CustomSourceWithoutChecksums_DownloadsAndComparesContent()
    {
        _server.Files["/my/ip.dat"] = Dat(GeoipCategories, extra: "-custom");
        _server.Files["/my/site.dat"] = Dat(GeositeCategories);
        _settings.Update(s => s with
        {
            Geo = s.Geo with
            {
                UseCustomSource = true,
                CustomGeoipUrl = new Uri(_server.BaseUri, "my/ip.dat").ToString(),
                CustomGeositeUrl = new Uri(_server.BaseUri, "my/site.dat").ToString(),
            },
        });
        using var updater = Create();

        Assert.Equal(GeoUpdateOutcome.Updated, await updater.UpdateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Dat(GeoipCategories, extra: "-custom"), Installed("geoip.dat"));

        Assert.Equal(GeoUpdateOutcome.UpToDate, await updater.UpdateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, _server.RequestCount("/my/ip.dat"));
    }

    [Fact]
    public async Task CustomSourceWithoutUrls_IsInvalidSource()
    {
        _settings.Update(s => s with { Geo = s.Geo with { UseCustomSource = true } });
        using var updater = Create();

        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateError.InvalidSource, updater.Status.LastError);
    }

    [Fact]
    public async Task WhileConnected_RestartsCore()
    {
        Publish("geoip.dat", Dat(GeoipCategories, extra: "-v2"));
        Publish("geosite.dat", Dat(GeositeCategories));
        _connection.SetState(ConnectionState.Connected);
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.Updated, outcome);
        Assert.Equal(1, _connection.ReconnectCalls);
    }

    [Fact]
    public async Task CoreFailsWithNewFiles_RestoresPreviousAndReconnects()
    {
        Publish("geoip.dat", Dat(GeoipCategories, extra: "-v2"));
        Publish("geosite.dat", Dat(GeositeCategories));
        _connection.SetState(ConnectionState.Connected);
        _connection.FailNextReconnect = true;
        using var updater = Create();

        var outcome = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GeoUpdateOutcome.Failed, outcome);
        Assert.Equal(GeoUpdateError.CoreRejected, updater.Status.LastError);
        Assert.Equal(Dat(GeoipCategories), Installed("geoip.dat"));
        Assert.Equal(1, _connection.ConnectCalls);
        Assert.Equal(ConnectionState.Connected, _connection.State);
    }

    [Fact]
    public async Task UpdateIfDue_AfterFreshCheck_DoesNothing()
    {
        Publish("geoip.dat", Dat(GeoipCategories));
        Publish("geosite.dat", Dat(GeositeCategories));
        using var updater = Create();
        await updater.UpdateAsync(TestContext.Current.CancellationToken);
        var requests = _server.Requests.Values.Sum();

        await updater.UpdateIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(requests, _server.Requests.Values.Sum());
    }

    [Fact]
    public void IsDue_RespectsAutoUpdateSwitch()
    {
        using var updater = Create();
        Assert.True(updater.IsDue(DateTimeOffset.UtcNow));

        _settings.Update(s => s with { Geo = s.Geo with { AutoUpdate = false } });

        Assert.False(updater.IsDue(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GeoDatFile_ReadsShippedFiles()
    {
        var geosite = Path.Combine(RepoPaths.GeoDirectory, "geosite.dat");
        Assert.SkipUnless(File.Exists(geosite), "assets/geo missing; run tools/fetch-xray.ps1");

        var categories = GeoDatFile.ReadCategories(geosite);

        Assert.Contains("ru-blocked", categories);
        Assert.Contains("category-ru", categories);
        Assert.Contains("ru", GeoDatFile.ReadCategories(Path.Combine(RepoPaths.GeoDirectory, "geoip.dat")));
    }

    [Fact]
    public void GeoDatFile_RejectsTruncatedFile()
    {
        var path = Path.Combine(_root.FullName, "bad.dat");
        File.WriteAllBytes(path, Dat(GeoipCategories)[..^3]);

        Assert.Throws<InvalidDataException>(() => GeoDatFile.ReadCategories(path));
    }

    private sealed class FakeConnection : IConnectionManager
    {
        public ConnectionState State { get; private set; }

        public ConnectionError? LastError => null;

        public (int Socks, int Http)? ActivePorts => null;

        public bool FailNextReconnect { get; set; }

        public bool DisconnectInsteadOfReconnect { get; set; }

        public int ReconnectCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        public event EventHandler<ConnectionState>? StateChanged;

        public void SetState(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(ConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default)
        {
            ReconnectCalls++;
            if (DisconnectInsteadOfReconnect)
            {
                SetState(ConnectionState.Disconnected);
            }
            else if (FailNextReconnect)
            {
                FailNextReconnect = false;
                SetState(ConnectionState.Error);
            }

            return Task.CompletedTask;
        }
    }
}
