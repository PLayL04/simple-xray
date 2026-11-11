namespace SimpleVpn.Core.Abstractions;

/// <summary>Administrator rights (needed for the TUN adapter). Implemented by the platform.</summary>
public interface IElevation
{
    /// <summary>True when the process runs with administrator rights.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Starts the app again with administrator rights (UAC prompt), asking it to connect right away.
    /// Returns false when the user declined the prompt; on true the caller must exit so the new instance can start.
    /// </summary>
    bool RestartElevated();
}
