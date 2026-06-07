using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DuckDNS (port of ddclient's <c>nic_duckdns_update</c>). One request carries both families:
/// Hostname is the comma-separated domain label(s), Password is the account token, and Server
/// optionally overrides the default endpoint. Login is unused.
/// </summary>
public sealed class DuckDnsProvider : DnsProviderBase
{
    private const string DefaultServer = "www.duckdns.org";

    /// <summary>Initializes a new instance of the <see cref="DuckDnsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DuckDnsProvider(IHttpClientFactory httpClientFactory, ILogger<DuckDnsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DuckDns;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("DuckDNS requires a domain (hostname) and a token (password).");
        }

        var url = ServerBase(record, DefaultServer)
            + "/update?domains=" + Uri.EscapeDataString(record.Hostname)
            + "&token=" + Uri.EscapeDataString(record.Password);
        if (record.UpdateIPv4 && ip.IPv4 is not null)
        {
            url += "&ip=" + Uri.EscapeDataString(ip.IPv4);
        }

        if (record.UpdateIPv6 && ip.IPv6 is not null)
        {
            url += "&ipv6=" + Uri.EscapeDataString(ip.IPv6);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        return string.Equals(result.Body.Trim(), "OK", StringComparison.Ordinal)
            ? DnsUpdateResult.Ok("DuckDNS accepted the update.")
            : DnsUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
