using Microsoft.Win32;
using SimpleXray.App.Services;

namespace SimpleXray.Desktop.Platform;

/// <summary>
/// Autostart through <c>HKCU\...\Run</c> (no administrator rights). The value holds the path of this copy
/// of the exe, so a portable folder that was moved shows autostart as off until it is enabled again.
/// </summary>
internal sealed class WindowsAutostart : IAutostart
{
    /// <summary>Passed by the Run entry: start minimized to the tray.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimpleXray";

    /// <summary>Run value written by versions before the rename to SimpleXray (0.1.1 and older).</summary>
    private const string LegacyValueName = "SimpleVpn";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Replaces the pre-rename <c>SimpleVpn</c> Run value: if it starts an exe in this folder (the old name or
    /// this one) or an exe that no longer exists, it is removed and, for this folder, recreated under the new name.
    /// Entries of other existing copies are left alone. Returns true when the entry was moved to this copy.
    /// </summary>
    public static bool MigrateLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is not string legacy)
        {
            return false;
        }

        var exe = ParseExePath(legacy);
        var ours = exe is not null && string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(exe)),
            Path.GetDirectoryName(Environment.ProcessPath),
            StringComparison.OrdinalIgnoreCase);
        if (!ours && exe is not null && File.Exists(exe))
        {
            return false;
        }

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (ours)
        {
            key.SetValue(ValueName, Command);
        }

        return ours;
    }

    private static string? ParseExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? command : command[..space];
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
