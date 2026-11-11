namespace SimpleVpn.Core.Models;

/// <summary>
/// Persisted while connected so a crash never leaves the user without internet:
/// on the next start the previous system proxy is restored.
/// </summary>
public sealed record RuntimeState
{
    /// <summary>True between applying our system proxy and restoring the previous one.</summary>
    public bool ProxyApplied { get; set; }

    /// <summary>The user's proxy settings before we changed them.</summary>
    public SystemProxySettings? PreviousProxy { get; set; }
}
