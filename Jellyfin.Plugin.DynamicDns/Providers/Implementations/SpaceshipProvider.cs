using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Spaceship (port of ddclient's <c>nic_spaceship_update</c>). Authenticates via the
/// <c>X-Api-Key</c> / <c>X-Api-Secret</c> headers: set login to the API key and password to the API
/// secret. Zone is the domain. The subdomain is derived from the hostname (<c>@</c> for the apex).
/// Per record type it lists the zone's records, deletes existing matches, then PUTs the new value.
/// </summary>
public sealed class SpaceshipProvider : DNSProviderBase
{
    private const string DefaultServer = "spaceship.dev";

    /// <summary>Initializes a new instance of the <see cref="SpaceshipProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SpaceshipProvider(IHttpClientFactory httpClientFactory, ILogger<SpaceshipProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Spaceship;

    /// <inheritdoc />
    public override string Label => "Spaceship";

    /// <inheritdoc />
    public override string Hint => "Login is the API key. Password is the API secret. Zone is the domain.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "API key",
        Password = "API secret",
        Zone = "Domain",
        Server = true,
        Ttl = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("Spaceship requires an API key (login) and API secret (password).");
        }

        // Derive the zone (domain) and the subdomain ('@' for the apex), mirroring ddclient.
        string domain;
        string subdomain;
        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            domain = record.Zone.Trim();
            if (string.Equals(record.Hostname, domain, StringComparison.OrdinalIgnoreCase))
            {
                subdomain = "@";
            }
            else if (record.Hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                subdomain = record.Hostname.Substring(0, record.Hostname.Length - domain.Length - 1);
                if (subdomain.Length == 0)
                {
                    subdomain = "@";
                }
            }
            else
            {
                return DNSUpdateResult.Fail("hostname '" + record.Hostname + "' does not end with zone '" + domain + "'");
            }
        }
        else
        {
            var dot = record.Hostname.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return DNSUpdateResult.Fail("cannot infer zone from '" + record.Hostname + "': no dot in hostname; set a zone.");
            }

            subdomain = record.Hostname.Substring(0, dot);
            domain = record.Hostname.Substring(dot + 1);
        }

        var server = ServerBase(record, DefaultServer);
        var headers = AuthHeaders(record);
        var basePath = server + "/api/v1/dns/records/" + Uri.EscapeDataString(domain);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SetRecordAsync(basePath, headers, subdomain, type, address, record.Ttl, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static List<KeyValuePair<string, string>> AuthHeaders(DNSRecord record)
    {
        return new List<KeyValuePair<string, string>>
        {
            new("Accept", "application/json"),
            new("X-Api-Key", record.Login),
            new("X-Api-Secret", record.Password)
        };
    }

    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return ": " + detail.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through to no detail.
        }

        return string.Empty;
    }

    private async Task<(bool Ok, string Message)> SetRecordAsync(
        string basePath,
        List<KeyValuePair<string, string>> headers,
        string subdomain,
        string rrtype,
        string ipValue,
        int ttl,
        CancellationToken cancellationToken)
    {
        var list = await SendAsync(
            HttpMethod.Get,
            basePath + "?take=500&skip=0",
            cancellationToken,
            headers).ConfigureAwait(false);
        if (!list.Ok)
        {
            return (false, "list failed (HTTP " + list.Status + Detail(list.Body) + ")");
        }

        List<string> addresses;
        try
        {
            addresses = ExistingAddresses(list.Body, rrtype, subdomain);
        }
        catch (JsonException)
        {
            return (false, "failed to parse record list");
        }

        foreach (var address in addresses)
        {
            var delBody = "[" + RecordObject(rrtype, subdomain, address, null) + "]";
            var del = await SendAsync(
                HttpMethod.Delete,
                basePath,
                cancellationToken,
                headers,
                body: delBody).ConfigureAwait(false);
            if (!del.Ok)
            {
                return (false, "delete failed (HTTP " + del.Status + Detail(del.Body) + ")");
            }
        }

        // force=true replaces any state remaining after the deletes.
        var putBody = "{\"force\":true,\"items\":[" + RecordObject(rrtype, subdomain, ipValue, ttl) + "]}";
        var put = await SendAsync(
            HttpMethod.Put,
            basePath,
            cancellationToken,
            headers,
            body: putBody).ConfigureAwait(false);
        return put.Ok
            ? (true, "set to " + ipValue)
            : (false, "update failed (HTTP " + put.Status + Detail(put.Body) + ")");
    }

    private static List<string> ExistingAddresses(string body, string rrtype, string subdomain)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return result;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), rrtype, StringComparison.Ordinal)
                && item.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), subdomain, StringComparison.Ordinal)
                && item.TryGetProperty("address", out var address)
                && address.ValueKind == JsonValueKind.String)
            {
                result.Add(address.GetString() ?? string.Empty);
            }
        }

        return result;
    }

    private static string RecordObject(string rrtype, string subdomain, string address, int? ttl)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":");
        AppendJsonString(sb, rrtype);
        sb.Append(",\"name\":");
        AppendJsonString(sb, subdomain);
        sb.Append(",\"address\":");
        AppendJsonString(sb, address);
        if (ttl is not null)
        {
            sb.Append(",\"ttl\":");
            sb.Append(ttl.Value.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
