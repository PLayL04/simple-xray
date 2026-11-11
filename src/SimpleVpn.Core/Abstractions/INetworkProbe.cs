namespace SimpleVpn.Core.Abstractions;

/// <summary>Local port allocation and readiness checks (abstracted for tests).</summary>
public interface INetworkProbe
{
    /// <summary>Returns <paramref name="preferred"/> if it is free on loopback, otherwise any free port not in <paramref name="exclude"/>.</summary>
    int AllocatePort(int preferred, IReadOnlyCollection<int> exclude);

    /// <summary>Waits until something accepts TCP connections on 127.0.0.1:<paramref name="port"/>.</summary>
    /// <param name="shouldAbort">Polled between attempts; returning true stops waiting (e.g. the core exited).</param>
    /// <returns>True when the port is accepting connections within <paramref name="timeout"/>.</returns>
    Task<bool> WaitForTcpAsync(int port, TimeSpan timeout, Func<bool> shouldAbort, CancellationToken cancellationToken = default);

    /// <summary>Waits until a network interface named <paramref name="name"/> is up (the TUN adapter).</summary>
    Task<bool> WaitForInterfaceAsync(string name, TimeSpan timeout, Func<bool> shouldAbort, CancellationToken cancellationToken = default);
}
