using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimpleVpn.Core.Tests.Geo;

/// <summary>Tiny HTTP/1.1 server on loopback: serves byte arrays by path, 404 otherwise, counts requests.</summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public TestHttpServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public ConcurrentDictionary<string, int> Requests { get; } = new();

    public Uri BaseUri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    public int RequestCount(string path) => Requests.GetValueOrDefault(path);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            var path = requestLine?.Split(' ')[1] ?? "/";
            Requests.AddOrUpdate(path, 1, (_, n) => n + 1);

            var found = Files.TryGetValue(path, out var body);
            body ??= [];
            var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
