using SimpleVpn.Core.Qr;

namespace SimpleVpn.App.Services;

/// <summary>Screenshot of all monitors, used to find a QR code shown on the screen.</summary>
public interface IScreenCapture
{
    /// <summary>Null when capturing is not possible (e.g. no desktop session).</summary>
    RawImage? Capture();
}
