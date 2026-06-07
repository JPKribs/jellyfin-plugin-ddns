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
/// IONOS DNS REST API. Port of ddclient's <c>nic_ionos_update</c>: looks up the zone for the host,
/// fetches existing A/AAAA records, then PUTs the matching record or POSTs a new one.
/// The API key (prefix.secret format) is supplied via <see cref="DnsRecord.Password"/> and sent
/// as the <c>X-API-Key</c> header.
/// </summary>
public sealed class IonosProvider : DnsProviderBase
{
    private const string DefaultServer = "api.hosting.ionos.com";

    /// <summary>Initializes a new instance of the <see cref="IonosProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public IonosProvider(IHttpClientFactory httpClientFactory, ILogger<IonosProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Ionos;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("IONOS requires an API key (prefix.secret) in the password field.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        var server = ServerBase(record, DefaultServer);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("accept", "application/json"),
            new("X-API-Key", record.Password)
        };

        // Find the zone the host falls under, then fetch its existing A/AAAA records.
        var zonesReply = await SendAsync(HttpMethod.Get, server + "/dns/v1/zones", cancellationToken, headers).ConfigureAwait(false);
        if (!zonesReply.Ok)
        {
            return DnsUpdateResult.Fail("zone list lookup failed (HTTP " + zonesReply.Status + ").");
        }

        var zoneId = FindZoneId(zonesReply.Body, record.Hostname);
        if (zoneId is null)
        {
            return DnsUpdateResult.Fail("no IONOS zone found for " + record.Hostname + ".");
        }

        var recordsReply = await SendAsync(
            HttpMethod.Get,
            server + "/dns/v1/zones/" + zoneId + "?suffix=" + Uri.EscapeDataString(record.Hostname) + "&recordType=A%2CAAAA",
            cancellationToken,
            headers).ConfigureAwait(false);
        if (!recordsReply.Ok)
        {
            return DnsUpdateResult.Fail("zone records lookup failed (HTTP " + recordsReply.Status + ").");
        }

        var ttl = record.Ttl > 1 ? record.Ttl : 300;
        var recordsBody = recordsReply.Body;
        var hostname = record.Hostname;

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpsertAsync(server, zoneId, headers, recordsBody, hostname, type, address, ttl, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpsertAsync(
        string server,
        string zoneId,
        List<KeyValuePair<string, string>> headers,
        string recordsBody,
        string host,
        string type,
        string ipValue,
        int ttl,
        CancellationToken cancellationToken)
    {
        var existingId = FindRecordId(recordsBody, host, type);
        var ttlText = ttl.ToString(CultureInfo.InvariantCulture);

        HttpMethod method;
        string url;
        string body;
        if (existingId is not null)
        {
            method = HttpMethod.Put;
            url = server + "/dns/v1/zones/" + zoneId + "/records/" + existingId;
            body = "{\"content\":\"" + ipValue + "\",\"ttl\":" + ttlText + ",\"prio\":0,\"disabled\":false}";
        }
        else
        {
            method = HttpMethod.Post;
            url = server + "/dns/v1/zones/" + zoneId + "/records";
            // host is user-supplied; JSON-encode it so a name with a quote/backslash can't corrupt the body.
            body = "[{\"name\":" + JsonSerializer.Serialize(host) + ",\"type\":\"" + type + "\",\"content\":\"" + ipValue
                + "\",\"ttl\":" + ttlText + ",\"prio\":0,\"disabled\":false}]";
        }

        var reply = await SendAsync(method, url, cancellationToken, headers, body).ConfigureAwait(false);
        return reply.Ok
            ? (true, "set to " + ipValue)
            : (false, "update failed (HTTP " + reply.Status + ")");
    }

    private static string? FindZoneId(string body, string host)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var zone in doc.RootElement.EnumerateArray())
            {
                if (!zone.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (string.Equals(host, name, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                {
                    if (zone.TryGetProperty("id", out var idElement))
                    {
                        return idElement.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? FindRecordId(string body, string host, string type)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var hostDot = host + ".";
            foreach (var item in records.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeElement)
                    || !string.Equals(typeElement.GetString(), type, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!item.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString() ?? string.Empty;
                if ((string.Equals(name, host, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, hostDot, StringComparison.OrdinalIgnoreCase))
                    && item.TryGetProperty("id", out var idElement))
                {
                    return idElement.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
