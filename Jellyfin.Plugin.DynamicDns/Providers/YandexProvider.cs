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
/// Yandex 360 / PDD (port of ddclient's <c>nic_yandex_update</c>). Authenticates with a
/// <c>PddToken</c> header, lists the domain's DNS records to find the record id whose
/// <c>fqdn</c> matches the hostname, then POSTs a form-encoded edit to set its content.
/// </summary>
public sealed class YandexProvider : DnsProviderBase
{
    private const string DefaultServer = "pddimp.yandex.ru";

    /// <summary>Initializes a new instance of the <see cref="YandexProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public YandexProvider(IHttpClientFactory httpClientFactory, ILogger<YandexProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Yandex;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login))
        {
            return DnsUpdateResult.Fail("A login (Yandex domain) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A password (Yandex PddToken) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        // ddclient updates a single 'wantip': prefer IPv4 when enabled, else IPv6.
        var wantIp = record.UpdateIPv4 ? ip.IPv4 : null;
        wantIp ??= record.UpdateIPv6 ? ip.IPv6 : null;
        if (wantIp is null)
        {
            return DnsUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var serverBase = ServerBase(record, DefaultServer);
        var domain = record.Login.Trim();

        var headers = new List<KeyValuePair<string, string>>
        {
            new("PddToken", record.Password)
        };

        // List records for the domain, then find the id whose fqdn matches the hostname.
        var listUrl = serverBase + "/api2/admin/dns/list?domain=" + Uri.EscapeDataString(domain);
        var listResult = await SendAsync(HttpMethod.Get, listUrl, cancellationToken, headers: headers).ConfigureAwait(false);
        if (!listResult.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + listResult.Status + " while listing records.");
        }

        string? recordId;
        try
        {
            using var doc = JsonDocument.Parse(listResult.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var successEl)
                || !string.Equals(successEl.GetString(), "ok", StringComparison.Ordinal))
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                return DnsUpdateResult.Fail("Yandex list failed: " + (error ?? "unknown error"));
            }

            recordId = FindRecordId(root, record.Hostname);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "{Provider} could not parse the record list response", Kind);
            return DnsUpdateResult.Fail("Could not parse the Yandex list response.");
        }

        if (string.IsNullOrEmpty(recordId))
        {
            return DnsUpdateResult.Fail("DNS record ID not found for " + record.Hostname + ".");
        }

        // Edit the record content to the new IP.
        var editUrl = serverBase + "/api2/admin/dns/edit";
        var body = "domain=" + Uri.EscapeDataString(domain)
            + "&record_id=" + Uri.EscapeDataString(recordId)
            + "&content=" + Uri.EscapeDataString(wantIp);

        var editResult = await SendAsync(
            HttpMethod.Post,
            editUrl,
            cancellationToken,
            headers: headers,
            body: body,
            contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);
        if (!editResult.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + editResult.Status + " while editing record.");
        }

        try
        {
            using var doc = JsonDocument.Parse(editResult.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var successEl)
                || !string.Equals(successEl.GetString(), "ok", StringComparison.Ordinal))
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                return DnsUpdateResult.Fail("Yandex edit failed: " + (error ?? "unknown error"));
            }
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "{Provider} could not parse the edit response", Kind);
            return DnsUpdateResult.Fail("Could not parse the Yandex edit response.");
        }

        return DnsUpdateResult.Ok("Updated " + record.Hostname + " to " + wantIp + ".");
    }

    private static string? FindRecordId(JsonElement root, string hostname)
    {
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in records.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (entry.TryGetProperty("fqdn", out var fqdnEl)
                && string.Equals(fqdnEl.GetString(), hostname, StringComparison.OrdinalIgnoreCase)
                && entry.TryGetProperty("record_id", out var idEl))
            {
                return idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetRawText()
                    : idEl.GetString();
            }
        }

        return null;
    }
}
