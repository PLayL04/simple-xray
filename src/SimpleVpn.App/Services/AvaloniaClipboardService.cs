using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace SimpleVpn.App.Services;

/// <summary>Clipboard of the app's main window / view (works for desktop and single-view lifetimes).</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync()
    {
        var topLevel = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime single => TopLevel.GetTopLevel(single.MainView),
            _ => null,
        };

        var clipboard = topLevel?.Clipboard;
        return clipboard is null ? null : await clipboard.TryGetTextAsync();
    }
}
