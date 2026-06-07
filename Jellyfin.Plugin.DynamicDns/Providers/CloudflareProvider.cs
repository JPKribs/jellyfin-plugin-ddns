using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Cloudflare v4 API. Port of ddclient's <c>nic_cloudflare_update</c>: looks up the zone ID,
/// finds the existing A/AAAA record, and PATCHes its content. Set login to <c>token</c> and
/// password to a scoped API token, or login to your account email and password to the global API key.
/// </summary>
public sealed class CloudflareProvider : DnsProviderBase
{
    private const string DefaultServer = "api.cloudflare.com/client/v4";

    /// <summary>Initializes a new instance of the <see cref="CloudflareProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public CloudflareProvider(IHttpClientFactory httpClientFactory, ILogger<CloudflareProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Cloudflare;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Zone) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Cloudflare requires a zone name and an API token/key.");
        }

        var server = ServerBase(record, DefaultServer);
        var headers = new List<KeyValuePair<string, string>>();
        if (string.Equals(record.Login, "token", StringComparison.Ordinal) || string.IsNullOrEmpty(record.Login))
        {
            headers.Add(new("Authorization", "Bearer " + record.Password));
        }
        else
        {
            headers.Add(new("X-Auth-Email", record.Login));
            headers.Add(new("X-Auth-Key", record.Password));
        }

        var zoneLookup = await SendAsync(
            HttpMethod.Get,
            server + "/zones/?name=" + Uri.EscapeDataString(record.Zone),
            cancellationToken,
            headers).ConfigureAwait(false);
        if (!zoneLookup.Ok)
        {
            return DnsUpdateResult.Fail("zone lookup failed (HTTP " + zoneLookup.Status + ").");
        }

        var zoneId = FindIdByName(zoneLookup.Body, record.Zone);
        if (zoneId is null)
        {
            return DnsUpdateResult.Fail("no zone ID found for zone " + record.Zone + ".");
        }

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PatchAsync(server, zoneId, headers, record.Hostname, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PatchAsync(
        string server,
        string zoneId,
        List<KeyValuePair<string, string>> headers,
        string host,
        string type,
        string ipValue,
        CancellationToken cancellationToken)
    {
        var lookup = await SendAsync(
            HttpMethod.Get,
            server + "/zones/" + zoneId + "/dns_records?type=" + type + "&name=" + Uri.EscapeDataString(host),
            cancellationToken,
            headers).ConfigureAwait(false);
        if (!lookup.Ok)
        {
            return (false, "record lookup failed (HTTP " + lookup.Status + ")");
        }

        var recordId = FindIdByName(lookup.Body, host);
        if (recordId is null)
        {
            return (false, "no '" + type + "' record at Cloudflare");
        }

        var patch = await SendAsync(
            HttpMethod.Patch,
            server + "/zones/" + zoneId + "/dns_records/" + recordId,
            cancellationToken,
            headers,
            body: "{\"content\":\"" + ipValue + "\"}").ConfigureAwait(false);

        return patch.Ok && HasResult(patch.Body)
            ? (true, "set to " + ipValue)
            : (false, "update failed (HTTP " + patch.Status + ")");
    }

    private static bool HasResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FindIdByName(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in result.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n)
                    && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("id", out var id))
                {
                    return id.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to null.
        }

        return null;
    }
}
