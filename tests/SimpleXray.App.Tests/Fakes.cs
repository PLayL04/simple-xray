using SimpleXray.App.Services;
using SimpleXray.Core.Abstractions;
using SimpleXray.Core.Connection;

namespace SimpleXray.App.Tests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; set; }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFiles : IFileService
{
    public string? OpenResult { get; set; }

    public string? Saved { get; private set; }

    public string? SavedName { get; private set; }

    public string? OpenedFolder { get; private set; }

    public Task<string?> OpenJsonFileAsync(string title, int maxBytes) => Task.FromResult(OpenResult);

    public Task<bool> SaveJsonFileAsync(string title, string suggestedFileName, string content)
    {
        SavedName = suggestedFileName;
        Saved = content;
        return Task.FromResult(true);
    }

    public Task OpenFolderAsync(string path)
    {
        OpenedFolder = path;
        return Task.CompletedTask;
    }

    public Core.Qr.RawImage? Image { get; set; }

    public Task<Core.Qr.RawImage?> OpenImageAsync(string title, int maxBytes) => Task.FromResult(Image);
}

internal sealed class FakeConnectionManager : IConnectionManager
{
    public ConnectionState State { get; private set; }

    public ConnectionError? LastError { get; set; }

    public (int Socks, int Http)? ActivePorts => State == ConnectionState.Connected ? (10888, 10889) : null;

    public int ConnectCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public int ReconnectCalls { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        Raise(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCalls++;
        Raise(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task ReconnectIfConnectedAsync(CancellationToken cancellationToken = default)
    {
        ReconnectCalls++;
        return Task.CompletedTask;
    }

    public void Raise(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

internal sealed class FakeElevation : IElevation
{
    public bool IsElevated { get; set; }

    public bool Accept { get; set; }

    public int RestartCalls { get; private set; }

    public bool RestartElevated()
    {
        RestartCalls++;
        return Accept;
    }
}

internal sealed class FakeLifetime : IAppLifetime
{
    public bool ShutdownCalled { get; private set; }

    public void Shutdown() => ShutdownCalled = true;
}

internal sealed class FakeAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

internal sealed class FakeScreen : IScreenCapture
{
    public Core.Qr.RawImage? Image { get; set; }

    public Core.Qr.RawImage? Capture() => Image;
}

internal static class QrImages
{
    /// <summary>A screenshot-like BGRA image with the text as a QR code (4 px per module, quiet zone).</summary>
    public static Core.Qr.RawImage Render(string text) => Render(Core.Qr.QrCodec.Encode(text));

    public static Core.Qr.RawImage Render(Core.Qr.QrMatrix code)
    {
        const int scale = 4, margin = 40;
        var size = (code.Size * scale) + (2 * margin);
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < code.Size * scale; y++)
        {
            for (var x = 0; x < code.Size * scale; x++)
            {
                if (code[x / scale, y / scale])
                {
                    var o = (((margin + y) * size) + margin + x) * 4;
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;
                }
            }
        }

        return new Core.Qr.RawImage(pixels, size, size);
    }
}
