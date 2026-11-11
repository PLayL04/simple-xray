namespace SimpleVpn.App.Services;

public interface IClipboardService
{
    /// <summary>Returns clipboard text, or null if empty / unavailable.</summary>
    Task<string?> GetTextAsync();
}
