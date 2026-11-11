namespace SimpleVpn.App.Services;

/// <summary>Command-line options that affect the UI after start.</summary>
public sealed record StartupOptions
{
    /// <summary>Connect as soon as the main window is shown (after an elevated restart for VPN mode).</summary>
    public bool ConnectOnStart { get; init; }
}
