namespace SimpleVpn.App.Services;

/// <summary>Start the app when the user signs in to the OS (per user, no administrator rights).</summary>
public interface IAutostart
{
    /// <summary>True when autostart points to this copy of the app.</summary>
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
