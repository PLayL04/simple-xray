using SimpleXray.Core.Models;

namespace SimpleXray.Core.Abstractions;

/// <summary>OS-wide proxy settings (WinINet on Windows).</summary>
public interface ISystemProxy
{
    SystemProxySettings GetCurrent();

    /// <summary>Applies the settings exactly (also used to restore a snapshot from <see cref="GetCurrent"/>).</summary>
    void Apply(SystemProxySettings settings);
}
