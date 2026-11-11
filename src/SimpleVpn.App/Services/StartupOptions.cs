namespace SimpleVpn.App.Services;

/// <summary>Command-line options that affect the UI after start.</summary>
public sealed record StartupOptions
{
    /// <summary>Connect right after start (after an elevated restart for VPN mode).</summary>
    public bool ConnectOnStart { get; init; }

    /// <summary>Started with the OS: stay in the tray, do not show the window.</summary>
    public bool StartHidden { get; init; }
}
