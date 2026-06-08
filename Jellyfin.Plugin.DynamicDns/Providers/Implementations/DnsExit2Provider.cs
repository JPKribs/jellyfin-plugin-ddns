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
/// DNSExit (port of ddclient's <c>nic_dnsexit2_update</c>). POSTs a single JSON document carrying the
/// API key, domain and one entry per enabled record type. Set <see cref="DNSRecord.Password"/> to the
/// API key and <see cref="DNSRecord.Zone"/> to the domain (defaults to the hostname if omitted).
/// </summary>
public sealed class DnsExit2Provider : DNSProviderBase
{
    private const string DefaultServer = "api.dnsexit.com";
    private const string Path = "/dns/";

    private static readonly KeyValuePair<string, string>[] JsonHeaders =
    {
        new("Accept", "application/json"),
    };

    private static readonly Dictionary<int, (string Status, string Message)> CodeMeaning = new()
    {
        [0] = ("good", "Success! Actions got executed successfully."),
        [1] = ("warning", "Some execution problems. May not indicate action failures."),
        [2] = ("badauth", "API Key Authentication Error. The API Key is missing or wrong."),
        [3] = ("error", "Missing Required Definitions. Your JSON file may be missing some required definitions."),
        [4] = ("error", "JSON Data Syntax Error. Your JSON file has a syntax error."),
        [5] = ("error", "JSON Defined Record Type not Supported."),
        [6] = ("error", "System Error. Our system problem. Contact support if you got such error."),
        [7] = ("error", "Error getting post data. The server had a problem receiving your JSON."),
    };

    /// <summary>Initializes a new instance of the <see cref="DnsExit2Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DnsExit2Provider(IHttpClientFactory httpClientFactory, ILogger<DnsExit2Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.DnsExit2;

    /// <inheritdoc />
    public override string Label => "DNSExit";

    /// <inheritdoc />
    public override string Hint => "Password is your DNSExit API key. Zone is the domain and defaults to the hostname. Hostname is the record.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record name",
        Password = "API key",
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
            return DNSUpdateResult.Fail("An API key (password) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        // ddclient defaults the zone to the hostname when it is not set.
        var zone = string.IsNullOrWhiteSpace(record.Zone) ? record.Hostname : record.Zone.Trim();

        // Trim the zone suffix from the hostname: matches "(?:^|\.)zone$". An exact match yields an empty name.
        if (!TryTrimZone(record.Hostname, zone, out var name))
        {
            return DNSUpdateResult.Fail("hostname does not end with the zone: " + zone);
        }

        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        var ipv6 = record.UpdateIPv6 ? ip.IPv6 : null;
        if (ipv4 is null && ipv6 is null)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        // ddclient uses a TTL default of 5 for this protocol.
        var ttl = record.Ttl > 0 ? record.Ttl : 5;

        var body = BuildBody(record.Password, zone, name, ttl, ipv4, ipv6);
        var url = ServerBase(record, DefaultServer) + Path;

        var reply = await SendAsync(
            HttpMethod.Post,
            url,
            cancellationToken,
            JsonHeaders,
            body,
            contentType: "application/json").ConfigureAwait(false);

        if (!reply.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + reply.Status + ".");
        }

        return Interpret(reply.Body, ipv4, ipv6);
    }

    private static bool TryTrimZone(string hostname, string zone, out string name)
    {
        if (string.Equals(hostname, zone, StringComparison.OrdinalIgnoreCase))
        {
            name = string.Empty;
            return true;
        }

        if (hostname.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
        {
            name = hostname.Substring(0, hostname.Length - zone.Length - 1);
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static string BuildBody(string apiKey, string zone, string name, int ttl, string? ipv4, string? ipv6)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("apikey", apiKey);
            writer.WriteString("domain", zone);
            writer.WriteStartArray("update");

            if (ipv4 is not null)
            {
                WriteEntry(writer, name, "A", ipv4, ttl);
            }

            if (ipv6 is not null)
            {
                WriteEntry(writer, name, "AAAA", ipv6, ttl);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter writer, string name, string type, string content, int ttl)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("content", content);
        writer.WriteNumber("ttl", ttl);
        writer.WriteEndObject();
    }

    private static DNSUpdateResult Interpret(string body, string? ipv4, string? ipv6)
    {
        int code;
        string serverMessage;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DNSUpdateResult.Fail("response is not a JSON object.");
            }

            if (!doc.RootElement.TryGetProperty("code", out var codeEl)
                || !doc.RootElement.TryGetProperty("message", out var messageEl))
            {
                return DNSUpdateResult.Fail("missing 'code' and 'message' properties in server response.");
            }

            code = codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : int.TryParse(codeEl.GetString(), out var parsed) ? parsed : -1;
            serverMessage = messageEl.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return DNSUpdateResult.Fail("could not parse server response as JSON.");
        }
        catch (FormatException)
        {
            return DNSUpdateResult.Fail("could not parse server response as JSON.");
        }

        if (!CodeMeaning.TryGetValue(code, out var meaning))
        {
            return DNSUpdateResult.Fail("unknown status code: " + code);
        }

        var (status, message) = meaning;
        if (string.Equals(status, "good", StringComparison.Ordinal))
        {
            var parts = new List<string>();
            if (ipv4 is not null)
            {
                parts.Add("A=" + ipv4);
            }

            if (ipv6 is not null)
            {
                parts.Add("AAAA=" + ipv6);
            }

            return DNSUpdateResult.Ok("good: updated " + string.Join(", ", parts));
        }

        if (string.Equals(status, "warning", StringComparison.Ordinal))
        {
            return DNSUpdateResult.Fail("warning: " + message + " (server: " + serverMessage + ")");
        }

        return DNSUpdateResult.Fail(status + ": " + message + " (server: " + serverMessage + ")");
    }
}
