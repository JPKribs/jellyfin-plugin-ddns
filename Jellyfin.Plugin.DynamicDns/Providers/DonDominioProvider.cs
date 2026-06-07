using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DonDominio (port of ddclient's <c>nic_dondominio_update</c>). Issues a GET per record type to
/// <c>/plain/?user=&amp;password=&amp;host=&amp;ip=</c>. <see cref="DnsRecord.Login"/> is the user and
/// <see cref="DnsRecord.Password"/> the API key; <see cref="DnsRecord.Hostname"/> is the host. The update
/// succeeds when the reply's last line contains <c>OK</c> or <c>IP:&lt;ip&gt;</c>.
/// </summary>
public sealed class DonDominioProvider : DnsProviderBase
{
    private const string DefaultServer = "dondns.dondominio.com";

    /// <summary>Initializes a new instance of the <see cref="DonDominioProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DonDominioProvider(IHttpClientFactory httpClientFactory, ILogger<DonDominioProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DonDominio;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login (user) and password (API key) are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SendUpdateAsync(server, record.Login, record.Password, record.Hostname, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(
        string server,
        string login,
        string password,
        string hostname,
        string address,
        CancellationToken cancellationToken)
    {
        var url = server
            + "/plain/?user=" + Uri.EscapeDataString(login)
            + "&password=" + Uri.EscapeDataString(password)
            + "&host=" + Uri.EscapeDataString(hostname)
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        var returned = LastLine(result.Body);
        if (returned.Contains("OK", StringComparison.Ordinal)
            || returned.Contains("IP:" + address, StringComparison.Ordinal))
        {
            return (true, "set to " + address);
        }

        return (false, "server said: " + returned);
    }

    private static string LastLine(string body)
    {
        var trimmed = (body ?? string.Empty).TrimEnd('\r', '\n', ' ', '\t');
        var nl = trimmed.LastIndexOf('\n');
        return nl < 0 ? trimmed.Trim() : trimmed[(nl + 1)..].Trim();
    }
}
