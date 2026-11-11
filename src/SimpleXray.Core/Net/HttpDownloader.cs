using System.Net;
using System.Text;
using SimpleXray.Core.Connection;

namespace SimpleXray.Core.Net;

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

    public DownloadException(HttpStatusCode statusCode)
        : base($"HTTP {(int)statusCode}")
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status when the server answered with an error; null for network failures.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>A network failure or server error that another route (the proxy) may avoid.</summary>
    public bool IsTransient => StatusCode is null || (int)StatusCode >= 500;
}

/// <summary>
/// HTTP(S) downloads (presets, geo files): first directly, then through our running proxy when the
/// direct attempt fails with a network error (e.g. GitHub is blocked). An HTTP 4xx answer is final.
/// </summary>
public sealed class HttpDownloader
{
    private readonly IConnectionManager _connection;

    public HttpDownloader(IConnectionManager connection)
    {
        _connection = connection;
    }

    /// <summary>Time to receive the response headers.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time without receiving any data while reading the body.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public static bool IsSupportedUrl(string? text, out Uri? uri) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Downloads a UTF-8 text document of at most <paramref name="maxBytes"/>. Throws <see cref="DownloadException"/>.</summary>
    public async Task<string> GetStringAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await WithFallbackAsync(
            (proxy, ct) =>
            {
                buffer.SetLength(0);
                return CopyAsync(uri, proxy, buffer, maxBytes, progress: null, ct);
            },
            cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Downloads to <paramref name="path"/> (overwritten). <paramref name="progress"/> receives 0..1 when the size is known.
    /// Throws <see cref="DownloadException"/>; a partial file is deleted.
    /// </summary>
    public async Task DownloadFileAsync(Uri uri, string path, long maxBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await WithFallbackAsync(
                async (proxy, ct) =>
                {
                    await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await CopyAsync(uri, proxy, file, maxBytes, progress, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private async Task WithFallbackAsync(Func<IWebProxy?, CancellationToken, Task> attempt, CancellationToken cancellationToken)
    {
        try
        {
            await attempt(null, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException ex) when (ex.IsTransient && _connection.ActivePorts is { } ports)
        {
            var proxy = new WebProxy(new Uri($"http://127.0.0.1:{ports.Http}"));
            await attempt(proxy, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(Uri uri, IWebProxy? proxy, Stream target, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // UseProxy=false for the direct attempt: the system proxy may point at our own core.
        using var handler = new HttpClientHandler { UseProxy = proxy is not null, Proxy = proxy };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleXray");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException(response.StatusCode);
            }

            var length = response.Content.Headers.ContentLength;
            if (length > maxBytes)
            {
                throw new DownloadException("Response is too large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var chunk = new byte[81920];
            long total = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                var read = await stream.ReadAsync(chunk, idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new DownloadException("Response is too large");
                }

                await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (length > 0)
                {
                    progress?.Report((double)total / length.Value);
                }
            }

            if (length is { } expected && total != expected)
            {
                throw new DownloadException("Connection closed before the download finished");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(ex.Message, ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new DownloadException(ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException("Timed out", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
