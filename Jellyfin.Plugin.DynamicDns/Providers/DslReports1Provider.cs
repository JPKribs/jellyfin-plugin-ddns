using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DSLReports (port of ddclient's <c>nic_dslreports1_update</c>). Single basic-auth GET whose body
/// must contain the return code NOERROR. Login/Password are the DSLReports credentials, Hostname is
/// the host id, and Server overrides the default endpoint.
/// </summary>
public sealed class DslReports1Provider : DnsProviderBase
{
    private const string DefaultServer = "www.dslreports.com";

    /// <summary>Initializes a new instance of the <see cref="DslReports1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DslReports1Provider(IHttpClientFactory httpClientFactory, ILogger<DslReports1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DslReports1;

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

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        return result.Body.Contains("NOERROR", StringComparison.OrdinalIgnoreCase)
            ? DnsUpdateResult.Ok("IP address set to " + value)
            : DnsUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
