using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Resolves a hostname's current DNS addresses so the update cycle can compare them against the detected
/// public IP. This catches a record that was changed from an external source, since the comparison is
/// against live DNS rather than a value the plugin stored.
/// </summary>
public sealed class DNSLookupService
{
    private readonly ILogger<DNSLookupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DNSLookupService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DNSLookupService(ILogger<DNSLookupService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the current A and AAAA addresses for a hostname.
    /// </summary>
    /// <param name="hostname">The hostname to resolve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved addresses, or a failed resolution when the lookup did not answer.</returns>
    public async Task<DNSResolution> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return DNSResolution.Failed;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname.Trim(), cancellationToken).ConfigureAwait(false);
            return DNSResolution.Resolved(addresses);
        }
        catch (SocketException ex)
        {
            // No such host, or the resolver could not answer. Treat it as not serving the address so the
            // record is pushed rather than wrongly skipped.
            _logger.LogDebug(ex, "DNS lookup for {Host} did not resolve", hostname);
            return DNSResolution.Failed;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "DNS lookup for {Host} was rejected as invalid", hostname);
            return DNSResolution.Failed;
        }
    }
}
