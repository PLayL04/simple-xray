using Microsoft.Win32;
using SimpleVpn.App.Services;

namespace SimpleVpn.Desktop.Platform;

/// <summary>
/// Autostart through <c>HKCU\...\Run</c> (no administrator rights). The value holds the path of this copy
/// of the exe, so a portable folder that was moved shows autostart as off until it is enabled again.
/// </summary>
internal sealed class WindowsAutostart : IAutostart
{
    /// <summary>Passed by the Run entry: start minimized to the tray.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimpleVpn";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else if (IsEnabled)
        {
            // Only remove our own entry; another copy of the app may own the value.
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
