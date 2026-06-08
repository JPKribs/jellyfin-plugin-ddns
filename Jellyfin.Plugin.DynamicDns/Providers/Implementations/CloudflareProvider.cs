using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Cloudflare v4 API. Port of ddclient's <c>nic_cloudflare_update</c>: looks up the zone ID,
/// finds the existing A/AAAA record, and PATCHes its content. Set login to <c>token</c> and
/// password to a scoped API token, or login to your account email and password to the global API key.
/// </summary>
public sealed class CloudflareProvider : DNSProviderBase
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
    public override DNSProviderKind Kind => DNSProviderKind.Cloudflare;

    /// <inheritdoc />
    public override string Label => "Cloudflare";

    /// <inheritdoc />
    public override string Hint => "Set Login to the word token for a scoped API token, or to your account email for a global key. Password is the token or global API key. Zone is the zone name such as example.com.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record name",
        Login = "Auth email or token",
        Password = "API token or key",
        Zone = "Zone name",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Zone) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("Cloudflare requires a zone name and an API token/key.");
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
            return DNSUpdateResult.Fail("zone lookup failed (HTTP " + zoneLookup.Status + ").");
        }

        var zoneId = FindIdByName(zoneLookup.Body, record.Zone);
        if (zoneId is null)
        {
            return DNSUpdateResult.Fail("no zone ID found for zone " + record.Zone + ".");
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

        var recordId = FindIdByName(lookup.Body, host, type);
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

    private static string? FindIdByName(string body, string name, string? expectedType = null)
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
                if (!item.TryGetProperty("name", out var n)
                    || !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // For a record lookup, also confirm the family so an A is never patched as an AAAA.
                if (expectedType is not null
                    && (!item.TryGetProperty("type", out var t)
                        || !string.Equals(t.GetString(), expectedType, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (item.TryGetProperty("id", out var id))
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
