using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SimpleXray.Core.Connection;
using SimpleXray.Core.Models;
using SimpleXray.Core.Net;
using SimpleXray.Core.Routing;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Geo;

/// <summary>Why a geo update failed. The UI maps codes to <c>GeoError_&lt;Code&gt;</c>.</summary>
public enum GeoUpdateError
{
    /// <summary>Custom source URLs are missing or not http(s).</summary>
    InvalidSource,

    DownloadFailed,

    /// <summary>The file does not match the published .sha256sum.</summary>
    ChecksumMismatch,

    /// <summary>Not a valid geoip/geosite file.</summary>
    InvalidFile,

    /// <summary>A category used by the active or a built-in preset is missing in the new file.</summary>
    MissingCategory,

    /// <summary>The files could not be replaced on disk.</summary>
    ReplaceFailed,

    /// <summary>The running core did not start with the new files; the previous ones were restored.</summary>
    CoreRejected,
}

public enum GeoUpdateOutcome
{
    UpToDate,
    Updated,
    Failed,
}

/// <summary>Persisted in <c>geo-state.json</c> (data directory).</summary>
public sealed record GeoState
{
    public DateTimeOffset? LastCheckUtc { get; set; }

    public DateTimeOffset? LastUpdateUtc { get; set; }
}

public sealed record GeoStatus
{
    public bool IsUpdating { get; init; }

    /// <summary>0..1 while downloading, when the size is known.</summary>
    public double? Progress { get; init; }

    public DateTimeOffset? LastCheckUtc { get; init; }

    public DateTimeOffset? LastUpdateUtc { get; init; }

    public GeoUpdateError? LastError { get; init; }

    public string? LastErrorDetail { get; init; }
}

/// <summary>
/// Safe geo file update: compare published sha256 with the local file → download to a temporary file →
/// verify sha256 and structure → check that preset categories still exist → replace atomically keeping
/// <c>.bak</c> → restart a running core, restoring the previous files if it fails.
/// </summary>
public sealed partial class GeoUpdater : IDisposable
{
    public const long MaxFileBytes = 300L * 1024 * 1024;

    private const string ChecksumSuffix = ".sha256sum";
    private const string DownloadSuffix = ".download";
    private const string BackupSuffix = ".bak";

    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly PresetRepository _presets;
    private readonly IConnectionManager _connection;
    private readonly HttpDownloader _downloader;
    private readonly ILogger<GeoUpdater> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statusLock = new();
    private GeoStatus _status;

    public GeoUpdater(
        AppPaths paths,
        SettingsStore settings,
        PresetRepository presets,
        IConnectionManager connection,
        HttpDownloader downloader,
        ILogger<GeoUpdater> logger)
    {
        _paths = paths;
        _settings = settings;
        _presets = presets;
        _connection = connection;
        _downloader = downloader;
        _logger = logger;
        var state = LoadState();
        _status = new GeoStatus { LastCheckUtc = state.LastCheckUtc, LastUpdateUtc = state.LastUpdateUtc };
    }

    /// <summary>Base URL of the default source (overridable for tests).</summary>
    public Uri DefaultBaseUri { get; init; } = new(GeoSettings.DefaultBaseUrl);

    /// <summary>Raised on any status change. May be raised on a background thread.</summary>
    public event EventHandler? StatusChanged;

    public GeoStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    private string StateFile => Path.Combine(_paths.DataDirectory, "geo-state.json");

    /// <summary>True when auto-update is on and the last successful check is older than the interval.</summary>
    public bool IsDue(DateTimeOffset nowUtc)
    {
        var geo = _settings.Settings.Geo;
        if (!geo.AutoUpdate)
        {
            return false;
        }

        var last = Status.LastCheckUtc;
        return last is null || nowUtc - last.Value >= TimeSpan.FromHours(Math.Max(1, geo.IntervalHours));
    }

    /// <summary>Checks the source and installs newer files. Never throws (except on cancellation); see <see cref="Status"/>.</summary>
    public Task<GeoUpdateOutcome> UpdateAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(onlyIfDue: false, cancellationToken);

    /// <summary>Scheduled variant: re-checks <see cref="IsDue"/> after waiting for a running update to finish.</summary>
    public Task<GeoUpdateOutcome> UpdateIfDueAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(onlyIfDue: true, cancellationToken);

    private async Task<GeoUpdateOutcome> UpdateAsync(bool onlyIfDue, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (onlyIfDue && !IsDue(DateTimeOffset.UtcNow))
        {
            _gate.Release();
            return GeoUpdateOutcome.UpToDate;
        }

        var staged = new List<(string Target, string Temp)>();
        try
        {
            SetStatus(s => s with { IsUpdating = true, Progress = null, LastError = null, LastErrorDetail = null });
            var outcome = await UpdateCoreAsync(staged, cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var state = LoadState() with { LastCheckUtc = now };
            if (outcome == GeoUpdateOutcome.Updated)
            {
                state = state with { LastUpdateUtc = now };
            }

            SaveState(state);
            SetStatus(s => s with { LastCheckUtc = state.LastCheckUtc, LastUpdateUtc = state.LastUpdateUtc });
            LogFinished(_logger, outcome);
            return outcome;
        }
        catch (GeoUpdateException ex)
        {
            LogFailed(_logger, ex.Error, ex.Detail);
            DateTimeOffset? checkedAt = null;
            if (IsPersistentFailure(ex.Error))
            {
                // The source answered, but with content we cannot use: retrying hourly would only re-download
                // ~100 MB (and, for CoreRejected, restart the core) again and again. Wait for the next interval.
                var state = LoadState() with { LastCheckUtc = DateTimeOffset.UtcNow };
                SaveState(state);
                checkedAt = state.LastCheckUtc;
            }

            SetStatus(s => s with { LastError = ex.Error, LastErrorDetail = ex.Detail, LastCheckUtc = checkedAt ?? s.LastCheckUtc });
            return GeoUpdateOutcome.Failed;
        }
        finally
        {
            foreach (var (_, temp) in staged)
            {
                TryDelete(temp);
            }

            SetStatus(s => s with { IsUpdating = false, Progress = null });
            _gate.Release();
        }
    }

    /// <summary>Failures that a retry within the hour will not fix (network errors and corrupted downloads may).</summary>
    private static bool IsPersistentFailure(GeoUpdateError error) =>
        error is GeoUpdateError.InvalidSource or GeoUpdateError.InvalidFile or GeoUpdateError.MissingCategory or GeoUpdateError.CoreRejected;

    private async Task<GeoUpdateOutcome> UpdateCoreAsync(List<(string Target, string Temp)> staged, CancellationToken cancellationToken)
    {
        var sources = ResolveSources(_settings.Settings.Geo);
        Directory.CreateDirectory(_paths.GeoDirectory);
        GeoFiles.EnsureAvailable(_paths);
        var required = RequiredCategories();

        for (var i = 0; i < sources.Count; i++)
        {
            var (name, uri, checksumRequired) = sources[i];
            var target = Path.Combine(_paths.GeoDirectory, name);
            var expected = await GetChecksumAsync(uri, checksumRequired, cancellationToken).ConfigureAwait(false);
            var current = File.Exists(target) ? await HashAsync(target, cancellationToken).ConfigureAwait(false) : null;
            if (expected is not null && expected == current)
            {
                continue;
            }

            var temp = target + DownloadSuffix;
            staged.Add((target, temp));
            var index = i;
            var progress = new Progress<double>(p => SetStatus(s => s.IsUpdating ? s with { Progress = (index + p) / sources.Count } : s));
            LogDownloading(_logger, uri);
            try
            {
                await _downloader.DownloadFileAsync(uri, temp, MaxFileBytes, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException ex)
            {
                throw new GeoUpdateException(GeoUpdateError.DownloadFailed, $"{name}: {ex.Message}");
            }

            var actual = await HashAsync(temp, cancellationToken).ConfigureAwait(false);
            if (expected is not null && actual != expected)
            {
                throw new GeoUpdateException(GeoUpdateError.ChecksumMismatch, name);
            }

            if (expected is null && actual == current)
            {
                // No checksum published and the content did not change.
                staged.RemoveAt(staged.Count - 1);
                TryDelete(temp);
                continue;
            }

            HashSet<string> categories;
            try
            {
                categories = GeoDatFile.ReadCategories(temp);
            }
            catch (InvalidDataException ex)
            {
                throw new GeoUpdateException(GeoUpdateError.InvalidFile, $"{name}: {ex.Message}");
            }

            var prefix = name.StartsWith("geoip", StringComparison.OrdinalIgnoreCase) ? "geoip" : "geosite";
            var present = TryReadCategories(target);
            var missing = required
                .Where(c => c.Kind == prefix && present?.Contains(c.Name) == true && !categories.Contains(c.Name))
                .Select(c => $"{prefix}:{c.Name}")
                .ToList();
            if (missing.Count > 0)
            {
                throw new GeoUpdateException(GeoUpdateError.MissingCategory, string.Join(", ", missing));
            }
        }

        if (staged.Count == 0)
        {
            return GeoUpdateOutcome.UpToDate;
        }

        Install(staged);

        if (_connection.State == ConnectionState.Connected)
        {
            await _connection.ReconnectIfConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (_connection.State == ConnectionState.Error)
            {
                LogCoreRejected(_logger);
                RestoreBackups(staged.Select(s => s.Target));
                await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                throw new GeoUpdateException(GeoUpdateError.CoreRejected);
            }
        }

        return GeoUpdateOutcome.Updated;
    }

    /// <summary>Replaces all staged files, keeping the previous versions as .bak; all or nothing.</summary>
    private static void Install(List<(string Target, string Temp)> staged)
    {
        var replaced = new List<string>();
        try
        {
            foreach (var (target, temp) in staged)
            {
                if (File.Exists(target))
                {
                    File.Replace(temp, target, target + BackupSuffix, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, target);
                }

                replaced.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreBackups(replaced);
            throw new GeoUpdateException(GeoUpdateError.ReplaceFailed, ex.Message);
        }
    }

    private static void RestoreBackups(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            var backup = target + BackupSuffix;
            if (File.Exists(backup))
            {
                File.Copy(backup, target, overwrite: true);
            }
        }
    }

    private async Task<string?> GetChecksumAsync(Uri fileUri, bool required, CancellationToken cancellationToken)
    {
        var checksumUri = new Uri(fileUri + ChecksumSuffix);
        string text;
        try
        {
            text = await _downloader.GetStringAsync(checksumUri, 4096, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException ex) when (!required && !ex.IsTransient)
        {
            return null;
        }
        catch (DownloadException ex)
        {
            throw new GeoUpdateException(GeoUpdateError.DownloadFailed, $"{checksumUri.AbsolutePath}: {ex.Message}");
        }

        // "<64 hex>  geoip.dat" (sha256sum format).
        var hash = text.Trim().Split((char[])[' ', '\t', '\r', '\n'], 2)[0].ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            if (required)
            {
                throw new GeoUpdateException(GeoUpdateError.DownloadFailed, $"{checksumUri.AbsolutePath}: not a sha256sum file");
            }

            return null;
        }

        return hash;
    }

    /// <summary>File name, URL and whether a .sha256sum must exist next to it.</summary>
    internal IReadOnlyList<(string Name, Uri Uri, bool ChecksumRequired)> ResolveSources(GeoSettings geo)
    {
        if (!geo.UseCustomSource)
        {
            var baseUri = DefaultBaseUri;
            return [("geoip.dat", new Uri(baseUri, "geoip.dat"), true), ("geosite.dat", new Uri(baseUri, "geosite.dat"), true)];
        }

        if (!HttpDownloader.IsSupportedUrl(geo.CustomGeoipUrl, out var geoip) || !HttpDownloader.IsSupportedUrl(geo.CustomGeositeUrl, out var geosite))
        {
            throw new GeoUpdateException(GeoUpdateError.InvalidSource);
        }

        return [("geoip.dat", geoip!, false), ("geosite.dat", geosite!, false)];
    }

    /// <summary>geosite/geoip categories used by the built-in presets and the active one.</summary>
    internal IReadOnlyList<(string Kind, string Name)> RequiredCategories()
    {
        var presets = BuiltInPresets.All.Append(_presets.Selected);
        var result = new HashSet<(string, string)>();
        foreach (var rule in presets.SelectMany(p => p.Rules).Where(r => r.Enabled))
        {
            foreach (var value in rule.Domain.Concat(rule.Ip))
            {
                var v = value.TrimStart('!');
                foreach (var kind in (string[])["geosite", "geoip"])
                {
                    if (v.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = v[(kind.Length + 1)..].Split('@')[0].ToLowerInvariant();
                        if (name.Length > 0)
                        {
                            result.Add((kind, name));
                        }
                    }
                }
            }
        }

        return [.. result];
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Categories of the installed file; null when it is missing or unreadable (then any valid file may replace it).</summary>
    private static HashSet<string>? TryReadCategories(string path)
    {
        try
        {
            return File.Exists(path) ? GeoDatFile.ReadCategories(path) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private GeoState LoadState() => JsonFile.Read(StateFile, CoreJsonContext.Default.GeoState) ?? new GeoState();

    private void SaveState(GeoState state) => JsonFile.Write(StateFile, state, CoreJsonContext.Default.GeoState);

    private void SetStatus(Func<GeoStatus, GeoStatus> change)
    {
        lock (_statusLock)
        {
            _status = change(_status);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Geo update: downloading {Uri}")]
    private static partial void LogDownloading(ILogger logger, Uri uri);

    [LoggerMessage(Level = LogLevel.Information, Message = "Geo update finished: {Outcome}")]
    private static partial void LogFinished(ILogger logger, GeoUpdateOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Geo update failed: {Error}. {Detail}")]
    private static partial void LogFailed(ILogger logger, GeoUpdateError error, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core did not start with the new geo files; restoring the previous ones")]
    private static partial void LogCoreRejected(ILogger logger);
}

public sealed class GeoUpdateException : Exception
{
    public GeoUpdateException(GeoUpdateError error, string? detail = null)
        : base(detail is null ? error.ToString() : $"{error}: {detail}")
    {
        Error = error;
        Detail = detail;
    }

    public GeoUpdateException()
        : this(GeoUpdateError.DownloadFailed)
    {
    }

    public GeoUpdateException(string message)
        : this(GeoUpdateError.DownloadFailed, message)
    {
    }

    public GeoUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = GeoUpdateError.DownloadFailed;
        Detail = message;
    }

    public GeoUpdateError Error { get; }

    public string? Detail { get; }
}
