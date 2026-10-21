using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// The country this server is in, from its public address and the location database. The address comes from
/// the last Dynamic DNS run when there is one, and is detected otherwise. Unknown until both are available.
/// </summary>
public sealed class ServerLocation
{
    private readonly IPDetectionService _detection;
    private readonly StatusStoreService _status;
    private readonly GeoIpService _geo;
    private readonly ILogger<ServerLocation> _logger;
    private GeoLocation _location = GeoLocation.Unknown;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerLocation"/> class.
    /// </summary>
    /// <param name="detection">The public address detection.</param>
    /// <param name="status">The Dynamic DNS status, holding the last detected address.</param>
    /// <param name="geo">The location lookup.</param>
    /// <param name="logger">The logger.</param>
    public ServerLocation(IPDetectionService detection, StatusStoreService status, GeoIpService geo, ILogger<ServerLocation> logger)
    {
        _detection = detection;
        _status = status;
        _geo = geo;
        _logger = logger;
    }

    /// <summary>Gets the server's location; empty fields while unknown.</summary>
    public GeoLocation Location => Volatile.Read(ref _location);

    /// <summary>
    /// Looks the server's public address up again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the lookup is done.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_geo.IsLoaded)
        {
            return;
        }

        var last = _status.Load();
        var found = Lookup(last.DetectedIPv4) ?? Lookup(last.DetectedIPv6);
        if (found is null)
        {
            var config = Plugin.Instance?.ReadConfiguration(c => c) ?? new PluginConfiguration();
            // IPv4 first; IPv6 is only asked for when IPv4 gives nothing, so an IPv4-only host is not left
            // failing IPv6 lookups every few minutes.
            var v4 = await _detection.DetectAsync(config, needIPv4: true, needIPv6: false, cancellationToken).ConfigureAwait(false);
            found = Lookup(v4.IPv4);
            if (found is null)
            {
                var v6 = await _detection.DetectAsync(config, needIPv4: false, needIPv6: true, cancellationToken).ConfigureAwait(false);
                found = Lookup(v6.IPv6);
            }
        }

        if (found is null)
        {
            _logger.LogDebug("The server's country is not known yet");
            return;
        }

        var before = Interlocked.Exchange(ref _location, found);
        if (!string.Equals(before.CountryCode, found.CountryCode, StringComparison.Ordinal))
        {
            _logger.LogInformation("This server is in {Country}", found.Country);
        }
    }

    private GeoLocation? Lookup(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var ip))
        {
            return null;
        }

        var location = _geo.Lookup(ip);
        return location.CountryCode.Length > 0 ? location : null;
    }
}
