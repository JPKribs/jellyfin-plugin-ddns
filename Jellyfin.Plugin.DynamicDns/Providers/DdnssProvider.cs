using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DDNSS.de (port of ddclient's <c>nic_ddnss_update</c>). <see cref="DnsRecord.Password"/> holds the
/// account key and <see cref="DnsRecord.Hostname"/> the host; IPv4 and IPv6 are updated in separate
/// requests. <see cref="DnsRecord.Server"/> overrides the default endpoint.
/// </summary>
public sealed class DdnssProvider : DnsProviderBase
{
    private const string DefaultServer = "ddnss.de";

    /// <summary>Initializes a new instance of the <see cref="DdnssProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DdnssProvider(IHttpClientFactory httpClientFactory, ILogger<DdnssProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Ddnss;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("DDNSS requires a host (hostname) and a key (password).");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(string server, DnsRecord record, string type, string value, CancellationToken cancellationToken)
    {
        // DDNSS uses distinct query params per family: ip= for A, ip6= for AAAA.
        var ipParam = string.Equals(type, "AAAA", StringComparison.Ordinal) ? "ip6=" : "ip=";
        var url = server + "/upd.php?key=" + Uri.EscapeDataString(record.Password)
            + "&host=" + Uri.EscapeDataString(record.Hostname)
            + "&" + ipParam + Uri.EscapeDataString(value);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        return result.Body.Contains("good", StringComparison.OrdinalIgnoreCase)
            ? (true, "set to " + value)
            : (false, "failed (" + FirstLine(result.Body) + ")");
    }
}
