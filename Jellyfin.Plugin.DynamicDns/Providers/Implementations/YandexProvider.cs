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
/// Yandex 360 / PDD (port of ddclient's <c>nic_yandex_update</c>). Authenticates with a
/// <c>PddToken</c> header, lists the domain's DNS records to find the record whose <c>fqdn</c> and
/// record type both match, then POSTs a form-encoded edit to set its content. Matching the type keeps
/// an IPv4 edit from overwriting the AAAA (or an unrelated TXT/MX) at the same name, and lets A and
/// AAAA update independently.
/// </summary>
public sealed class YandexProvider : DNSProviderBase
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
    public override DNSProviderKind Kind => DNSProviderKind.Yandex;

    /// <inheritdoc />
    public override string Label => "Yandex";

    /// <inheritdoc />
    public override string Hint => "Login is the domain. Password is your PDD token. Hostname is the FQDN.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "FQDN",
        Login = "Domain",
        Password = "PDD token",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login))
        {
            return DNSUpdateResult.Fail("A login (Yandex domain) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A password (Yandex PddToken) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var serverBase = ServerBase(record, DefaultServer);
        var domain = record.Login.Trim();
        var hostname = record.Hostname.Trim();

        var headers = new List<KeyValuePair<string, string>>
        {
            new("PddToken", record.Password)
        };

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateTypeAsync(serverBase, domain, headers, hostname, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateTypeAsync(
        string serverBase,
        string domain,
        List<KeyValuePair<string, string>> headers,
        string hostname,
        string type,
        string ipValue,
        CancellationToken cancellationToken)
    {
        // List records for the domain, then find the id whose fqdn and type both match the target.
        var listUrl = serverBase + "/api2/admin/dns/list?domain=" + Uri.EscapeDataString(domain);
        var listResult = await SendAsync(HttpMethod.Get, listUrl, cancellationToken, headers: headers).ConfigureAwait(false);
        if (!listResult.Ok)
        {
            return (false, "HTTP " + listResult.Status + " while listing records");
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
                return (false, "list failed: " + (error ?? "unknown error"));
            }

            recordId = FindRecordId(root, hostname, type);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "{Provider} could not parse the record list response", Kind);
            return (false, "could not parse the list response");
        }

        if (string.IsNullOrEmpty(recordId))
        {
            // The edit endpoint cannot create records, so a missing one must be created at Yandex first.
            return (false, "no existing " + type + " record found for " + hostname + "; create it at Yandex first");
        }

        // Edit the record content to the new IP.
        var editUrl = serverBase + "/api2/admin/dns/edit";
        var body = "domain=" + Uri.EscapeDataString(domain)
            + "&record_id=" + Uri.EscapeDataString(recordId)
            + "&content=" + Uri.EscapeDataString(ipValue);

        var editResult = await SendAsync(
            HttpMethod.Post,
            editUrl,
            cancellationToken,
            headers: headers,
            body: body,
            contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);
        if (!editResult.Ok)
        {
            return (false, "HTTP " + editResult.Status + " while editing record");
        }

        try
        {
            using var doc = JsonDocument.Parse(editResult.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var successEl)
                || !string.Equals(successEl.GetString(), "ok", StringComparison.Ordinal))
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                return (false, "edit failed: " + (error ?? "unknown error"));
            }
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "{Provider} could not parse the edit response", Kind);
            return (false, "could not parse the edit response");
        }

        return (true, "set to " + ipValue);
    }

    private static string? FindRecordId(JsonElement root, string hostname, string type)
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
                && entry.TryGetProperty("type", out var typeEl)
                && string.Equals(typeEl.GetString(), type, StringComparison.OrdinalIgnoreCase)
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
