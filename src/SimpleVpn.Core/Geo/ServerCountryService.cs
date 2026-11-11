using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.Core.Geo;

/// <summary>
/// Fills <see cref="ServerProfile.CountryCode"/> in the background for servers whose name has no flag:
/// the address (resolved with the system DNS when it is a domain) is looked up in the local geoip.dat.
/// No external services. Each server is tried once per app run; the result is saved in the profile.
/// </summary>
public sealed partial class ServerCountryService : IDisposable
{
    private readonly ProfileRepository _profiles;
    private readonly AppPaths _paths;
    private readonly ILogger<ServerCountryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<(Guid, string)> _attempted = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    public ServerCountryService(ProfileRepository profiles, AppPaths paths, ILogger<ServerCountryService> logger)
    {
        _profiles = profiles;
        _paths = paths;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _profiles.Changed += OnProfilesChanged;
        Schedule();
    }

    /// <summary>Resolves pending profiles now (also used by tests).</summary>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A fresh data folder gets geoip.dat only on the first connect; copy the shipped one now.
            var geoip = Path.Combine(_paths.GeoDirectory, "geoip.dat");
            if (!File.Exists(geoip) && !GeoFiles.EnsureAvailable(_paths))
            {
                return;
            }

            foreach (var profile in _profiles.Profiles)
            {
                if (profile.CountryCode is not null || CountryFlags.CodeFromName(profile.Name) is not null
                    || !_attempted.Add((profile.Id, profile.Address)))
                {
                    continue;
                }

                var code = await ResolveAsync(geoip, profile.Address, cancellationToken).ConfigureAwait(false);
                if (code is not null)
                {
                    _profiles.SetCountry(profile.Id, code);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ResolveAsync(string geoip, string host, CancellationToken cancellationToken)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var address))
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
            }

            return address is null ? null : await Task.Run(() => GeoIpCountry.Find(geoip, address), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Not worth bothering the user: the server is just shown without a flag.
            LogLookupFailed(_logger, ex);
            return null;
        }
    }

    private void Schedule() => _ = Task.Run(async () =>
    {
        try
        {
            await UpdateAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // App is exiting.
        }
    });

    private void OnProfilesChanged(object? sender, EventArgs e) => Schedule();

    public void Dispose()
    {
        _profiles.Changed -= OnProfilesChanged;
        _stop.Cancel();
        _stop.Dispose();
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server country lookup failed")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);
}
