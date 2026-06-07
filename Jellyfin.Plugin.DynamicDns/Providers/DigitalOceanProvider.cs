using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DigitalOcean DNS API v2. Port of ddclient's <c>nic_digitalocean_update</c>: authenticates with a
/// Bearer personal access token, looks up the existing A/AAAA record by name, and PATCHes its data when
/// the IP has changed. Set password to the DigitalOcean API token and zone to the apex domain
/// (e.g. <c>example.com</c>); hostname is the record name.
/// </summary>
public sealed class DigitalOceanProvider : DnsProviderBase
{
    private const string DefaultServer = "api.digitalocean.com";

    /// <summary>Initializes a new instance of the <see cref="DigitalOceanProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DigitalOceanProvider(IHttpClientFactory httpClientFactory, ILogger<DigitalOceanProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DigitalOcean;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("DigitalOcean requires an API token (password).");
        }

        if (string.IsNullOrWhiteSpace(record.Zone))
        {
            return DnsUpdateResult.Fail("DigitalOcean requires a zone (apex domain).");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("DigitalOcean requires a hostname.");
        }

        var server = ServerBase(record, DefaultServer);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", "Bearer " + record.Password),
        };

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateOneAsync(server, headers, record, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateOneAsync(
        string server,
        List<KeyValuePair<string, string>> headers,
        DnsRecord record,
        string type,
        string ipValue,
        CancellationToken cancellationToken)
    {
        var listUrl = string.Concat(
            server,
            "/v2/domains/",
            Uri.EscapeDataString(record.Zone),
            "/records?name=",
            Uri.EscapeDataString(record.Hostname),
            "&type=",
            type);

        var list = await SendAsync(HttpMethod.Get, listUrl, cancellationToken, headers).ConfigureAwait(false);
        if (!list.Ok)
        {
            return (false, "listing failed (HTTP " + list.Status.ToString(CultureInfo.InvariantCulture) + ")");
        }

        if (!TryGetSingleRecord(list.Body, out var recordId, out var currentIp))
        {
            return (false, "listing failed (no record, multiple records, or malformed JSON)");
        }

        if (string.Equals(currentIp, ipValue, StringComparison.Ordinal))
        {
            return (true, "already " + ipValue + ", no update needed");
        }

        var updateUrl = string.Concat(
            server,
            "/v2/domains/",
            Uri.EscapeDataString(record.Zone),
            "/records/",
            recordId);

        var body = "{\"type\":\"" + type + "\",\"data\":\"" + ipValue + "\"}";
        var update = await SendAsync(HttpMethod.Patch, updateUrl, cancellationToken, headers, body: body).ConfigureAwait(false);

        return update.Ok
            ? (true, "set to " + ipValue)
            : (false, "update failed (HTTP " + update.Status.ToString(CultureInfo.InvariantCulture) + ")");
    }

    private static bool TryGetSingleRecord(string body, out string recordId, out string currentIp)
    {
        recordId = string.Empty;
        currentIp = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("domain_records", out var records)
                || records.ValueKind != JsonValueKind.Array
                || records.GetArrayLength() != 1)
            {
                return false;
            }

            var elem = records[0];
            if (elem.ValueKind != JsonValueKind.Object
                || !elem.TryGetProperty("id", out var id)
                || !elem.TryGetProperty("data", out var data))
            {
                return false;
            }

            recordId = id.ValueKind == JsonValueKind.Number
                ? id.GetRawText()
                : id.GetString() ?? string.Empty;
            currentIp = data.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(recordId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
