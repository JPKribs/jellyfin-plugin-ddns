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
/// NS1 / IBM NS1 Connect (ns1.com). Port of ddclient's <c>nic_ns1_update</c>: GETs the existing record at
/// <c>/zones/{zone}/{host}/{type}</c>, then POSTs to update it or PUTs to create it. Set
/// <c>record.Login</c> to your NS1 API key (sent as the <c>X-NSONE-Key</c> header). <c>record.Zone</c>
/// names the zone (inferred from the hostname when blank). <c>record.Server</c> overrides the API base.
/// </summary>
public sealed class Ns1Provider : DNSProviderBase
{
    private const string DefaultServer = "api.nsone.net/v1";

    private static readonly KeyValuePair<string, string>[] AcceptHeader =
    {
        new("Accept", "application/json"),
    };

    /// <summary>Initializes a new instance of the <see cref="Ns1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public Ns1Provider(IHttpClientFactory httpClientFactory, ILogger<Ns1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Ns1;

    /// <inheritdoc />
    public override string Label => "NS1";

    /// <inheritdoc />
    public override string Hint => "Login is your NS1 API key. Zone is optional and is inferred from the hostname if blank.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record name",
        Login = "API key",
        Zone = "Zone",
        Server = true,
        Ttl = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login))
        {
            return DNSUpdateResult.Fail("NS1 requires an API key (set it as the login).");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("NS1 requires a hostname.");
        }

        var host = record.Hostname.Trim();

        string domain;
        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            domain = record.Zone.Trim();
            if (!host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return DNSUpdateResult.Fail("hostname '" + host + "' does not end with zone '" + domain + "'.");
            }
        }
        else
        {
            var labels = host.Split('.');
            if (labels.Length < 2)
            {
                return DNSUpdateResult.Fail("cannot infer zone from '" + host + "': no dot in hostname; set a zone.");
            }

            domain = labels.Length == 2
                ? host
                : host[(host.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }

        var server = ServerBase(record, DefaultServer);
        var ttl = record.Ttl > 1 ? record.Ttl : 300;
        var headers = BuildHeaders(record.Login);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateTypeAsync(server, domain, host, type, address, ttl, headers, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateTypeAsync(
        string server,
        string domain,
        string host,
        string rrtype,
        string ipValue,
        int ttl,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var path = "/zones/" + Uri.EscapeDataString(domain) + "/" + Uri.EscapeDataString(host) + "/" + rrtype;

        var get = await SendAsync(HttpMethod.Get, server + path, cancellationToken, headers).ConfigureAwait(false);
        if (get.Status == 0)
        {
            return (false, "failed (could not connect)");
        }

        // ddclient treats a 404 on GET as "record does not exist yet" (empty hash).
        bool exists;
        if (get.Status == 404)
        {
            exists = false;
        }
        else if (!get.Ok)
        {
            return (false, "API error " + get.Status.ToString(CultureInfo.InvariantCulture) + MessageDetail(get.Body));
        }
        else
        {
            exists = HasType(get.Body);
        }

        var body = "{\"answers\":[{\"answer\":[\"" + ipValue + "\"]}],\"ttl\":" + ttl.ToString(CultureInfo.InvariantCulture) + "}";
        var method = exists ? HttpMethod.Post : HttpMethod.Put;

        var update = await SendAsync(method, server + path, cancellationToken, headers, body).ConfigureAwait(false);
        if (update.Status == 0)
        {
            return (false, "failed (could not connect)");
        }

        return update.Ok
            ? (true, "set to " + ipValue)
            : (false, "API error " + update.Status.ToString(CultureInfo.InvariantCulture) + MessageDetail(update.Body));
    }

    private static List<KeyValuePair<string, string>> BuildHeaders(string apiKey)
    {
        return new List<KeyValuePair<string, string>>(AcceptHeader)
        {
            new("X-NSONE-Key", apiKey),
        };
    }

    /// <summary>Returns true when the GET payload is a JSON object carrying a "type" field (record exists).</summary>
    private static bool HasType(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string MessageDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return ": " + message.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore. No structured detail available.
        }

        return string.Empty;
    }
}
