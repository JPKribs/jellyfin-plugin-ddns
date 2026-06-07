using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// ChangeIP (port of ddclient's <c>nic_changeip_update</c>). Login/Password are the basic-auth
/// credentials, Hostname is the record to update, and Server optionally overrides the default endpoint.
/// </summary>
public sealed class ChangeIpProvider : DnsProviderBase
{
    private const string DefaultServer = "nic.changeip.com";

    /// <summary>Initializes a new instance of the <see cref="ChangeIpProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ChangeIpProvider(IHttpClientFactory httpClientFactory, ILogger<ChangeIpProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.ChangeIp;

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

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SendUpdateAsync(record, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(DnsRecord record, string type, string address, CancellationToken cancellationToken)
    {
        var url = ServerBase(record, DefaultServer)
            + "/nic/update?hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        var responseBody = result.Body ?? string.Empty;
        if (responseBody.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, type + " set to " + address);
        }

        return (false, "invalid reply: " + FirstLine(responseBody));
    }
}
