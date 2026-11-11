using SimpleVpn.Core.Storage;

namespace SimpleVpn.Desktop.Platform;

internal static class WindowsAppPaths
{
    /// <summary>%APPDATA%\SimpleVpn and %LOCALAPPDATA%\SimpleVpn, or .\data in portable mode.</summary>
    public static AppPaths Resolve() => AppPaths.Resolve(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists);
}
