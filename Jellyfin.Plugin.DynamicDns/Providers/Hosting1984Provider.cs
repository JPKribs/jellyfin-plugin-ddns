using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// 1984 Hosting (port of ddclient's <c>nic_1984_update</c>). Sends a GET to
/// <c>/1.0/freedns/?apikey=&amp;domain=&amp;ip=</c> using <see cref="DnsRecord.Password"/> as the API key
/// and <see cref="DnsRecord.Hostname"/> as the domain; the JSON <c>ok</c> field signals success.
/// </summary>
public sealed class Hosting1984Provider : DnsProviderBase
{
    private const string DefaultServer = "api.1984.is";

    /// <summary>Initializes a new instance of the <see cref="Hosting1984Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public Hosting1984Provider(IHttpClientFactory httpClientFactory, ILogger<Hosting1984Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Hosting1984;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("An API key (password) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(DnsRecord record, string address, CancellationToken cancellationToken)
    {
        var url = ServerBase(record, DefaultServer)
            + "/1.0/freedns/?apikey=" + Uri.EscapeDataString(record.Password)
            + "&domain=" + Uri.EscapeDataString(record.Hostname)
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        bool ok;
        string? msg = null;
        string? responseIp = null;
        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            var root = doc.RootElement;

            ok = root.TryGetProperty("ok", out var okElement)
                && (okElement.ValueKind == JsonValueKind.True
                    || (okElement.ValueKind == JsonValueKind.String
                        && string.Equals(okElement.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

            if (root.TryGetProperty("msg", out var msgElement) && msgElement.ValueKind == JsonValueKind.String)
            {
                msg = msgElement.GetString();
            }

            if (root.TryGetProperty("ip", out var ipElement) && ipElement.ValueKind == JsonValueKind.String)
            {
                responseIp = ipElement.GetString();
            }
        }
        catch (JsonException)
        {
            return (false, "Could not parse the JSON response.");
        }

        if (!ok)
        {
            return (false, string.IsNullOrWhiteSpace(msg) ? "update rejected" : msg);
        }

        var reported = string.IsNullOrWhiteSpace(responseIp) ? address : responseIp;

        // 1984 reports an unchanged record via "unaltered" in msg rather than a distinct status.
        return !string.IsNullOrEmpty(msg) && msg.Contains("unaltered", StringComparison.OrdinalIgnoreCase)
            ? (true, "skipped: IP was already set to " + reported)
            : (true, "updated successfully to " + reported);
    }
}
