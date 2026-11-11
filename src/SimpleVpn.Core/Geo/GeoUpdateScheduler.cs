using Microsoft.Extensions.Logging;

namespace SimpleVpn.Core.Geo;

/// <summary>
/// Background auto-update: shortly after start and then every <see cref="CheckPeriod"/> it runs
/// <see cref="GeoUpdater.UpdateIfDueAsync"/> when <see cref="GeoUpdater.IsDue"/>. A failed check does not move
/// the "last check" time, so it is retried on the next tick.
/// </summary>
public sealed partial class GeoUpdateScheduler : IDisposable
{
    private readonly GeoUpdater _updater;
    private readonly ILogger<GeoUpdateScheduler> _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public GeoUpdateScheduler(GeoUpdater updater, ILogger<GeoUpdateScheduler> logger)
    {
        _updater = updater;
        _logger = logger;
    }

    /// <summary>Delay after start, so the first check does not compete with app startup and connecting.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CheckPeriod { get; init; } = TimeSpan.FromHours(1);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_updater.IsDue(DateTimeOffset.UtcNow))
                {
                    await _updater.UpdateIfDueAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(CheckPeriod, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A background loop must never crash the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLoopFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Geo auto-update loop stopped")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}
