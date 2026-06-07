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
/// Porkbun (port of ddclient's <c>nic_porkbun_update</c>). For each enabled record type it looks up the
/// existing A/AAAA record via <c>retrieveByNameType</c>, then PATCHes its content via <c>editByNameType</c>,
/// preserving the record's existing TTL and notes. Set <see cref="DnsRecord.Login"/> to the Porkbun
/// <c>apikey</c> and <see cref="DnsRecord.Password"/> to the <c>secretapikey</c>. Optionally set
/// <see cref="DnsRecord.Zone"/> to the root domain; otherwise the hostname is split on the first dot.
/// </summary>
public sealed class PorkbunProvider : DnsProviderBase
{
    private const string DefaultServer = "api.porkbun.com";

    private static readonly IReadOnlyList<KeyValuePair<string, string>> JsonHeaders =
        new List<KeyValuePair<string, string>> { new("Accept", "application/json") };

    /// <summary>Initializes a new instance of the <see cref="PorkbunProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public PorkbunProvider(IHttpClientFactory httpClientFactory, ILogger<PorkbunProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Porkbun;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Porkbun requires an apikey (login) and secretapikey (password).");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        string domain;
        string subDomain;
        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            domain = record.Zone;
            if (string.Equals(record.Hostname, domain, StringComparison.OrdinalIgnoreCase))
            {
                subDomain = string.Empty;
            }
            else if (record.Hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                subDomain = record.Hostname.Substring(0, record.Hostname.Length - domain.Length - 1);
            }
            else
            {
                return DnsUpdateResult.Fail("hostname does not end with the root-domain value: " + domain);
            }
        }
        else
        {
            var dot = record.Hostname.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return DnsUpdateResult.Fail("hostname must contain a domain, or set a zone (root-domain).");
            }

            subDomain = record.Hostname.Substring(0, dot);
            domain = record.Hostname.Substring(dot + 1);
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateTypeAsync(server, domain, subDomain, type, address, record, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAuthBody(DnsRecord record)
    {
        // Login => apikey, Password => secretapikey.
        return "{\"secretapikey\":" + JsonSerializer.Serialize(record.Password)
            + ",\"apikey\":" + JsonSerializer.Serialize(record.Login) + "}";
    }

    private static string BuildEditBody(DnsRecord record, string content, string? ttl, string? notes)
    {
        var ttlJson = ttl is null ? "null" : JsonSerializer.Serialize(ttl);
        var notesJson = notes is null ? "null" : JsonSerializer.Serialize(notes);
        return "{\"secretapikey\":" + JsonSerializer.Serialize(record.Password)
            + ",\"apikey\":" + JsonSerializer.Serialize(record.Login)
            + ",\"content\":" + JsonSerializer.Serialize(content)
            + ",\"ttl\":" + ttlJson
            + ",\"notes\":" + notesJson + "}";
    }

    private async Task<(bool Ok, string Message)> UpdateTypeAsync(
        string server,
        string domain,
        string subDomain,
        string type,
        string ipValue,
        DnsRecord record,
        CancellationToken cancellationToken)
    {
        var retrieveUrl = server + "/api/json/v3/dns/retrieveByNameType/"
            + Uri.EscapeDataString(domain) + "/" + type + "/" + Uri.EscapeDataString(subDomain);

        var retrieve = await SendAsync(
            HttpMethod.Post,
            retrieveUrl,
            cancellationToken,
            JsonHeaders,
            body: BuildAuthBody(record)).ConfigureAwait(false);

        if (!retrieve.Ok)
        {
            return (false, "lookup failed (HTTP " + retrieve.Status + ")");
        }

        string? recordId;
        string? existingContent;
        string? ttl;
        string? notes;
        try
        {
            using var doc = JsonDocument.Parse(retrieve.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusEl)
                || !string.Equals(statusEl.GetString(), "SUCCESS", StringComparison.Ordinal))
            {
                return (false, "unexpected status");
            }

            if (!root.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array
                || records.GetArrayLength() == 0)
            {
                return (false, "no applicable existing records");
            }

            var first = records[0];
            recordId = first.TryGetProperty("id", out var idEl) ? GetAsString(idEl) : null;
            if (recordId is null)
            {
                return (false, "no applicable existing records");
            }

            existingContent = first.TryGetProperty("content", out var contentEl) ? GetAsString(contentEl) : null;
            ttl = first.TryGetProperty("ttl", out var ttlEl) ? GetAsString(ttlEl) : null;
            notes = first.TryGetProperty("notes", out var notesEl) ? GetAsString(notesEl) : null;
        }
        catch (JsonException)
        {
            return (false, "unexpected service response");
        }

        if (string.Equals(existingContent, ipValue, StringComparison.Ordinal))
        {
            return (true, "skipped: already set to " + ipValue);
        }

        var editUrl = server + "/api/json/v3/dns/editByNameType/"
            + Uri.EscapeDataString(domain) + "/" + type + "/" + Uri.EscapeDataString(subDomain);

        var edit = await SendAsync(
            HttpMethod.Post,
            editUrl,
            cancellationToken,
            JsonHeaders,
            body: BuildEditBody(record, ipValue, ttl, notes)).ConfigureAwait(false);

        if (!edit.Ok)
        {
            return (false, "update failed (HTTP " + edit.Status + ")");
        }

        // Porkbun returns HTTP 200 with {"status":"ERROR"} on logical failures, so the body must be
        // checked too — an HTTP-status-only check records a rejected edit as success.
        if (!IsSuccessStatus(edit.Body))
        {
            return (false, "update rejected by Porkbun");
        }

        return (true, "set to " + ipValue);
    }

    /// <summary>Returns true when a Porkbun JSON response carries <c>"status":"SUCCESS"</c>.</summary>
    private static bool IsSuccessStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "SUCCESS", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}
