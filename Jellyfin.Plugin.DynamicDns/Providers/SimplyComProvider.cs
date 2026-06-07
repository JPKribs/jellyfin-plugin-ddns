using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Simply.com (port of ddclient's <c>nic_simplycom_update</c>). Issues a basic-auth GET against
/// <c>/nic/update</c> per enabled record type, sending the IP via <c>myip</c>. Login is the account,
/// password the API key, Hostname the record, and Zone (optional) the <c>domain</c> scope.
/// </summary>
public sealed class SimplyComProvider : DnsProviderBase
{
    private const string DefaultServer = "dyndns.simply.com";

    /// <summary>Initializes a new instance of the <see cref="SimplyComProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SimplyComProvider(IHttpClientFactory httpClientFactory, ILogger<SimplyComProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.SimplyCom;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login (account) and password (API key) are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateOneAsync(record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateOneAsync(DnsRecord record, string address, CancellationToken cancellationToken)
    {
        var url = ServerBase(record, DefaultServer) + "/nic/update"
            + "?hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(address);

        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            url += "&domain=" + Uri.EscapeDataString(record.Zone);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        var status = FirstToken(result.Body);
        if (status.StartsWith("good", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "good (" + address + ").");
        }

        if (status.StartsWith("nochg", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "nochg; address already current (" + address + ").");
        }

        return (false, "server said: " + FirstLine(result.Body));
    }
}
