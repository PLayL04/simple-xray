using SimpleVpn.App.Services;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Connection;

namespace SimpleVpn.App.Tests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; set; }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);
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
