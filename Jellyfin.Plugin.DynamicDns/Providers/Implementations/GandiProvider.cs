using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Gandi LiveDNS API. Port of ddclient's <c>nic_gandi_update</c>: for each enabled record type it
/// GETs the current rrset for the host under the zone, skips the update when the value (and TTL, when
/// configured) already match, otherwise PUTs the new rrset value. Set <see cref="DNSRecord.Password"/>
/// to the Gandi API key or personal access token. Set <see cref="DNSRecord.Login"/> to <c>token</c> to
/// send an <c>Authorization: Bearer</c> header (personal access token). Any other value sends
/// <c>Authorization: Apikey</c>. <see cref="DNSRecord.Zone"/> is the domain and the hostname is the
/// record name relative to that zone.
/// </summary>
public sealed class GandiProvider : DNSProviderBase
{
    private const string DefaultServer = "api.gandi.net";
    private const string ScriptPath = "/v5";

    /// <summary>Initializes a new instance of the <see cref="GandiProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public GandiProvider(IHttpClientFactory httpClientFactory, ILogger<GandiProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Gandi;

    /// <inheritdoc />
    public override string Label => "Gandi LiveDNS";

    /// <inheritdoc />
    public override string Hint => "Password is a Gandi API key. Or set Login to the word token with a personal access token. Zone is the domain.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record name",
        Login = "Auth keyword",
        Password = "API key or token",
        Zone = "Domain",
        Server = true,
        Ttl = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("Gandi requires an API key or personal access token in the password field.");
        }

        if (string.IsNullOrWhiteSpace(record.Zone))
        {
            return DNSUpdateResult.Fail("Gandi requires the domain (zone).");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("Gandi requires a hostname.");
        }

        var zone = record.Zone.Trim();
        var hostname = StripZoneSuffix(record.Hostname.Trim(), zone);

        var scheme = string.Equals(record.Login, "token", StringComparison.OrdinalIgnoreCase) ? "Bearer" : "Apikey";
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", scheme + " " + record.Password),
        };

        var baseUrl = ServerBase(record, DefaultServer) + ScriptPath + "/livedns/domains/"
            + Uri.EscapeDataString(zone) + "/records/" + Uri.EscapeDataString(hostname) + "/";

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateTypeAsync(baseUrl, type, address, record.Ttl, headers, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static string StripZoneSuffix(string host, string zone)
    {
        var suffix = "." + zone;
        if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return host.Substring(0, host.Length - suffix.Length);
        }

        if (string.Equals(host, zone, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        return host;
    }

    private async Task<(bool Ok, string Message)> UpdateTypeAsync(
        string baseUrl,
        string type,
        string ipValue,
        int ttl,
        List<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + type;

        var current = await SendAsync(HttpMethod.Get, url, cancellationToken, headers).ConfigureAwait(false);
        if (current.Ok && AlreadySet(current.Body, ipValue, ttl))
        {
            return (true, "already set to " + ipValue);
        }

        var put = await SendAsync(
            HttpMethod.Put,
            url,
            cancellationToken,
            headers,
            body: BuildBody(ipValue, ttl)).ConfigureAwait(false);

        if (put.Ok)
        {
            return (true, "updated to " + ipValue);
        }

        return (false, "update failed (HTTP " + put.Status + "): " + ExtractMessage(put.Body));
    }

    private static string BuildBody(string ipValue, int ttl)
    {
        // ddclient only sends rrset_ttl when a ttl is configured. Treat <= 0 as "not configured".
        if (ttl > 0)
        {
            return string.Concat(
                "{\"rrset_ttl\":",
                ttl.ToString(CultureInfo.InvariantCulture),
                ",\"rrset_values\":[\"",
                ipValue,
                "\"]}");
        }

        return "{\"rrset_values\":[\"" + ipValue + "\"]}";
    }

    private static bool AlreadySet(string body, string ipValue, int ttl)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("rrset_values", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() == 0)
            {
                return false;
            }

            var first = values[0];
            if (first.ValueKind != JsonValueKind.String
                || !string.Equals(first.GetString(), ipValue, StringComparison.Ordinal))
            {
                return false;
            }

            // When no TTL is configured, ddclient ignores the server's TTL for the "skip" decision.
            if (ttl <= 0)
            {
                return true;
            }

            return root.TryGetProperty("rrset_ttl", out var ttlElement)
                && ttlElement.ValueKind == JsonValueKind.Number
                && ttlElement.TryGetInt32(out var current)
                && current == ttl;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return FirstLine(body);
    }
}
