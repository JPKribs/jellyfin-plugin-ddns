using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// DuckDNS (port of ddclient's <c>nic_duckdns_update</c>). One request carries both families:
/// Hostname is the comma-separated domain label(s), Password is the account token, and Server
/// optionally overrides the default endpoint. Login is unused.
/// </summary>
public sealed class DuckDnsProvider : DNSProviderBase
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
    public override DNSProviderKind Kind => DNSProviderKind.DuckDns;

    /// <inheritdoc />
    public override string Label => "Duck DNS";

    /// <inheritdoc />
    public override string Hint => "Hostname is your DuckDNS labels. Password is your account token. Login and Zone are unused.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Labels",
        Password = "Token",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("DuckDNS requires a domain (hostname) and a token (password).");
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
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        return string.Equals(result.Body.Trim(), "OK", StringComparison.Ordinal)
            ? DNSUpdateResult.Ok("DuckDNS accepted the update.")
            : DNSUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
