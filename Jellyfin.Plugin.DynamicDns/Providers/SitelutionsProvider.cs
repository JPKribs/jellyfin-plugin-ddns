using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Sitelutions (port of ddclient's <c>nic_sitelutions_update</c>). A GET to <c>/dnsup</c> per record
/// type with the record id, email login, password and IP as query parameters. <see cref="DnsRecord.Hostname"/>
/// is the numeric record id, <see cref="DnsRecord.Login"/> the account email and
/// <see cref="DnsRecord.Password"/> the password.
/// </summary>
public sealed class SitelutionsProvider : DnsProviderBase
{
    private const string DefaultServer = "api2.sitelutions.com";

    /// <summary>Initializes a new instance of the <see cref="SitelutionsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SitelutionsProvider(IHttpClientFactory httpClientFactory, ILogger<SitelutionsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Sitelutions;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A record id (hostname) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login (email) and password are required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record.Hostname, record.Login, record.Password, record.Ttl, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string hostname,
        string login,
        string password,
        int ttl,
        string address,
        CancellationToken cancellationToken)
    {
        var url = server
            + "/dnsup?id=" + Uri.EscapeDataString(hostname)
            + "&user=" + Uri.EscapeDataString(login)
            + "&pass=" + Uri.EscapeDataString(password)
            + "&ip=" + Uri.EscapeDataString(address);

        if (ttl > 0)
        {
            url += "&ttl=" + ttl.ToString(CultureInfo.InvariantCulture);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        // Sitelutions signals success only by the literal word "success" appearing in the reply.
        return result.Body.Contains("success", StringComparison.OrdinalIgnoreCase)
            ? (true, "updated to " + address)
            : (false, "invalid reply: " + FirstLine(result.Body));
    }
}
