using System.Net;
using System.Text;
using SimpleVpn.Core.Connection;

namespace SimpleVpn.Core.Net;

public sealed class DownloadException : Exception
{
    public DownloadException()
    {
    }

    public DownloadException(string message)
        : base(message)
    {
    }

    public DownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Small HTTP(S) downloads (presets now, geo files later): first directly, then through our running
/// proxy when the direct attempt fails (e.g. GitHub is blocked).
/// </summary>
public sealed class HttpDownloader
{
    private readonly IConnectionManager _connection;

    public HttpDownloader(IConnectionManager connection)
    {
        _connection = connection;
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public static bool IsSupportedUrl(string? text, out Uri? uri) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Downloads a UTF-8 text document of at most <paramref name="maxBytes"/>. Throws <see cref="DownloadException"/>.</summary>
    public async Task<string> GetStringAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            return await GetStringAsync(uri, proxy: null, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException) when (_connection.ActivePorts is { } ports)
        {
            var proxy = new WebProxy(new Uri($"http://127.0.0.1:{ports.Http}"));
            return await GetStringAsync(uri, proxy, maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetStringAsync(Uri uri, IWebProxy? proxy, int maxBytes, CancellationToken cancellationToken)
    {
        // UseProxy=false for the direct attempt: the system proxy may point at our own core.
        using var handler = new HttpClientHandler { UseProxy = proxy is not null, Proxy = proxy };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleVpn");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException($"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new DownloadException("Response is too large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new DownloadException("Response is too large");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException("Timed out", ex);
        }
    }
}
