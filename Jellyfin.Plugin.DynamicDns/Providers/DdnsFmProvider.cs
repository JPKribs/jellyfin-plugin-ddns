using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DDNS.fm (port of ddclient's <c>nic_ddnsfm_update</c>). Hostname is the domain and Password holds the
/// update key; IPv4 and IPv6 are pushed in separate requests.
/// </summary>
public sealed class DdnsFmProvider : DnsProviderBase
{
    private const string DefaultServer = "https://api.ddns.fm";

    /// <summary>Initializes a new instance of the <see cref="DdnsFmProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DdnsFmProvider(IHttpClientFactory httpClientFactory, ILogger<DdnsFmProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DdnsFm;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("DDNS.fm requires a domain (hostname) and a key (password).");
        }

        var server = ServerBase(record, DefaultServer);
        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(string server, DnsRecord record, string value, CancellationToken cancellationToken)
    {
        var url = server + "/update?key=" + Uri.EscapeDataString(record.Password)
            + "&domain=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(value);
        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        return result.Ok ? (true, "set to " + value) : (false, "HTTP " + result.Status);
    }
}
