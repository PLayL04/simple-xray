namespace SimpleXray.Core.Storage;

/// <summary>
/// File system layout of the application. Platform layers resolve the root folders
/// and pass them to <see cref="Resolve"/>; everything else is derived here.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "SimpleXray";

    /// <summary>If a file with this name exists next to the executable, all data is stored in <c>.\data</c>.</summary>
    public const string PortableMarkerFileName = "portable";

    private AppPaths(string appDirectory, string configDirectory, string dataDirectory, bool isPortable)
    {
        AppDirectory = appDirectory;
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    /// <summary>Directory of the executable (read-only at runtime).</summary>
    public string AppDirectory { get; }

    /// <summary>User configuration: settings, profiles, custom presets (roaming).</summary>
    public string ConfigDirectory { get; }

    /// <summary>Machine-local data: geo files, generated core config, logs, runtime state.</summary>
    public string DataDirectory { get; }

    public bool IsPortable { get; }

    /// <summary>Bundled Xray binaries (xray.exe, wintun.dll).</summary>
    public string CoreDirectory => Path.Combine(AppDirectory, "core");

    /// <summary>Geo files shipped with the build; used until the first successful update.</summary>
    public string SeedGeoDirectory => Path.Combine(AppDirectory, "geo");

    public string GeoDirectory => Path.Combine(DataDirectory, "geo");
    public string RuntimeDirectory => Path.Combine(DataDirectory, "run");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string StateFile => Path.Combine(DataDirectory, "state.json");

    public string PresetsDirectory => Path.Combine(ConfigDirectory, "presets");
    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public string ProfilesFile => Path.Combine(ConfigDirectory, "profiles.json");

    /// <param name="appDirectory">Directory containing the executable.</param>
    /// <param name="roamingRoot">Per-user roaming root (e.g. %APPDATA%).</param>
    /// <param name="localRoot">Per-user local root (e.g. %LOCALAPPDATA%).</param>
    /// <param name="fileExists">File existence probe (injected for testability).</param>
    public static AppPaths Resolve(string appDirectory, string roamingRoot, string localRoot, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (fileExists(Path.Combine(appDirectory, PortableMarkerFileName)))
        {
            var portableRoot = Path.Combine(appDirectory, "data");
            return new AppPaths(appDirectory, portableRoot, portableRoot, isPortable: true);
        }

        return new AppPaths(
            appDirectory,
            Path.Combine(roamingRoot, AppFolderName),
            Path.Combine(localRoot, AppFolderName),
            isPortable: false);
    }

    /// <summary>Creates all writable directories.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { ConfigDirectory, PresetsDirectory, DataDirectory, GeoDirectory, RuntimeDirectory, LogsDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
