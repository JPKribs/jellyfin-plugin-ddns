using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Legacy DynDNS v1 protocol (port of ddclient's <c>nic_dyndns1_update</c>). Sends one basic-auth GET
/// covering a single address and treats a "return code: NOERROR" body as success.
/// Login/Password are the account credentials, Hostname is the host to update, and Server overrides the
/// update endpoint (default <c>members.dyndns.org</c>).
/// </summary>
public sealed class DynDns1Provider : DnsProviderBase
{
    private const string DefaultServer = "members.dyndns.org";

    /// <summary>Initializes a new instance of the <see cref="DynDns1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DynDns1Provider(IHttpClientFactory httpClientFactory, ILogger<DynDns1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DynDns1;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login and password are required.");
        }

        var value = (record.UpdateIPv4 ? ip.IPv4 : null) ?? (record.UpdateIPv6 ? ip.IPv6 : null);
        if (value is null)
        {
            return DnsUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var service = record.Static ? "statdns" : "dyndns";
        var url = ServerBase(record, DefaultServer) + "/nic/" + service
            + "?action=edit&started=1&hostname=YES&host_id=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(value);
        if (record.Wildcard)
        {
            url += "&wildcard=ON";
        }

        if (!string.IsNullOrWhiteSpace(record.Mx))
        {
            url += "&mx=" + Uri.EscapeDataString(record.Mx) + "&backmx=" + (record.BackupMx ? "YES" : "NO");
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var body = result.Body;
        var ok = body.Contains("return code", StringComparison.OrdinalIgnoreCase)
            && body.Contains("NOERROR", StringComparison.OrdinalIgnoreCase)
            && !body.Contains("error code: ABUSE", StringComparison.OrdinalIgnoreCase);

        return ok
            ? DnsUpdateResult.Ok("IP address set to " + value)
            : DnsUpdateResult.Fail("server said: " + FirstLine(body));
    }
}
