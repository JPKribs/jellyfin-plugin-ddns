using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// OVH (port of ddclient's <c>nic_ovh_update</c>). Sends a DynDNS-style GET to
/// <c>/nic/update?system=dyndns&amp;hostname=&amp;myip=</c> using HTTP basic auth with
/// <see cref="DnsRecord.Login"/>/<see cref="DnsRecord.Password"/>; <see cref="DnsRecord.Hostname"/> is the
/// record to update and <see cref="DnsRecord.Server"/> overrides the endpoint host. A reply containing
/// <c>good</c> or <c>nochg</c> is success.
/// </summary>
public sealed class OvhProvider : DnsProviderBase
{
    private const string DefaultServer = "www.ovh.com";
    private const string Script = "/nic/update";

    /// <summary>Initializes a new instance of the <see cref="OvhProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public OvhProvider(IHttpClientFactory httpClientFactory, ILogger<OvhProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Ovh;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login and password are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        var ipv6 = record.UpdateIPv6 ? ip.IPv6 : null;

        // OVH's DynDNS endpoint accepts a single 'myip' value per update (ddclient's $wantip).
        var wantip = ipv4 ?? ipv6;
        if (wantip is null)
        {
            return DnsUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var url = ServerBase(record, DefaultServer) + Script
            + "?system=dyndns&hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(wantip);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (result.Status == 0)
        {
            return DnsUpdateResult.Fail("Could not connect to the OVH server.");
        }

        if (!result.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var body = result.Body ?? string.Empty;
        var nochg = body.Contains("nochg", StringComparison.OrdinalIgnoreCase);
        var good = body.Contains("good", StringComparison.OrdinalIgnoreCase);

        if (good)
        {
            return DnsUpdateResult.Ok("IP address set to " + wantip + ".");
        }

        if (nochg)
        {
            return DnsUpdateResult.Ok("Skipped: IP address was already set to " + wantip + ".");
        }

        return DnsUpdateResult.Fail("server said: " + FirstLine(body));
    }
}
