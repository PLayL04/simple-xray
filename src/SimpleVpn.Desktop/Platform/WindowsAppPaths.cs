using SimpleVpn.Core.Storage;

namespace SimpleVpn.Desktop.Platform;

internal static class WindowsAppPaths
{
    /// <summary>
    /// .\data in portable mode (the release zip ships the <c>portable</c> marker), otherwise
    /// %APPDATA%\SimpleVpn and %LOCALAPPDATA%\SimpleVpn. A portable copy unpacked into a read-only folder
    /// (e.g. Program Files) falls back to the profile folders instead of failing at start.
    /// </summary>
    public static AppPaths Resolve()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = AppPaths.Resolve(AppContext.BaseDirectory, roaming, local, File.Exists);
        return paths.IsPortable && !CanWrite(paths.DataDirectory)
            ? AppPaths.Resolve(AppContext.BaseDirectory, roaming, local, _ => false)
            : paths;
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
