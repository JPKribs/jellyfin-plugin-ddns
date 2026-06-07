using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Njalla (port of ddclient's <c>nic_njalla_update</c>). A single GET to
/// <c>/update/?h=&lt;hostname&gt;&amp;k=&lt;password&gt;</c> sets both A and AAAA at once.
/// Credentials: <see cref="DnsRecord.Password"/> is the per-record dynamic-DNS key; no login is used.
/// </summary>
public sealed class NjallaProvider : DnsProviderBase
{
    private const string DefaultServer = "njal.la";

    /// <summary>Initializes a new instance of the <see cref="NjallaProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public NjallaProvider(IHttpClientFactory httpClientFactory, ILogger<NjallaProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Njalla;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A password (dynamic DNS key) is required.");
        }

        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        var ipv6 = record.UpdateIPv6 ? ip.IPv6 : null;

        var url = new StringBuilder(ServerBase(record, DefaultServer));
        url.Append("/update/?h=").Append(Uri.EscapeDataString(record.Hostname));
        url.Append("&k=").Append(Uri.EscapeDataString(record.Password));

        var detail = new StringBuilder();
        if (ipv4 is not null)
        {
            url.Append("&a=").Append(Uri.EscapeDataString(ipv4));
            detail.Append(" IPv4: ").Append(ipv4);
        }

        if (ipv6 is not null)
        {
            url.Append("&aaaa=").Append(Uri.EscapeDataString(ipv6));
            detail.Append(" IPv6: ").Append(ipv6);
        }

        if (ipv4 is null && ipv6 is null)
        {
            // No address supplied: &auto tells Njalla to derive the IP from the request source.
            url.Append("&auto");
            detail.Append(" auto");
        }

        var result = await SendAsync(HttpMethod.Get, url.ToString(), cancellationToken).ConfigureAwait(false);
        if (result.Status == 0)
        {
            return DnsUpdateResult.Fail("could not connect to " + ServerBase(record, DefaultServer));
        }

        int status;
        string message;
        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            var root = doc.RootElement;
            status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
            message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return DnsUpdateResult.Fail("Unknown response: " + FirstLine(result.Body));
        }

        if (status == 401 && message.Contains("invalid host or key", StringComparison.OrdinalIgnoreCase))
        {
            return DnsUpdateResult.Fail("Invalid host or key");
        }

        if (status == 200 && message.Contains("record updated", StringComparison.OrdinalIgnoreCase))
        {
            return DnsUpdateResult.Ok("IP address set to" + detail.ToString());
        }

        return DnsUpdateResult.Fail("Unknown response: " + (message.Length > 0 ? message : FirstLine(result.Body)));
    }
}
