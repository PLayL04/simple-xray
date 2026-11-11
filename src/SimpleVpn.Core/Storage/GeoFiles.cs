namespace SimpleVpn.Core.Storage;

/// <summary>geoip.dat / geosite.dat used by Xray routing.</summary>
public static class GeoFiles
{
    public static IReadOnlyList<string> Required { get; } = ["geoip.dat", "geosite.dat"];

    /// <summary>
    /// Makes sure every required file exists in <see cref="AppPaths.GeoDirectory"/>,
    /// copying missing ones from the seed files shipped with the app.
    /// </summary>
    /// <returns>False if a file is missing both in the data directory and in the seed.</returns>
    public static bool EnsureAvailable(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.GeoDirectory);

        foreach (var name in Required)
        {
            var target = Path.Combine(paths.GeoDirectory, name);
            if (File.Exists(target))
            {
                continue;
            }

            var seed = Path.Combine(paths.SeedGeoDirectory, name);
            if (!File.Exists(seed))
            {
                return false;
            }

            File.Copy(seed, target);
        }

        return true;
    }
}
