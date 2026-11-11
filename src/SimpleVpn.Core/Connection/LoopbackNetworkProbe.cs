using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SimpleVpn.Core.Abstractions;

namespace SimpleVpn.Core.Connection;

public sealed class LoopbackNetworkProbe : INetworkProbe
{
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public int AllocatePort(int preferred, IReadOnlyCollection<int> exclude)
    {
        ArgumentNullException.ThrowIfNull(exclude);

        if (preferred is > 0 and <= 65535 && !exclude.Contains(preferred) && IsFree(preferred))
        {
            return preferred;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!exclude.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free local port found.");
    }

    public async Task<bool> WaitForTcpAsync(int port, TimeSpan timeout, Func<bool> shouldAbort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shouldAbort);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (shouldAbort())
            {
                return false;
            }

            using var client = new TcpClient();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(ConnectAttemptTimeout);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
