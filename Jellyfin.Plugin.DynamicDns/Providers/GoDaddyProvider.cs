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
/// GoDaddy (port of ddclient's <c>nic_godaddy_update</c>). The API key is the API token: place the
/// key in <see cref="DnsRecord.Login"/> and the secret in <see cref="DnsRecord.Password"/>; these are
/// combined into the <c>Authorization: sso-key login:password</c> header. <see cref="DnsRecord.Zone"/>
/// holds the domain and is stripped from the hostname to form the record name.
/// </summary>
public sealed class GoDaddyProvider : DnsProviderBase
{
    private const string DefaultServer = "api.godaddy.com/v1/domains";

    private static readonly IReadOnlyList<KeyValuePair<string, string>> AcceptHeader =
        new[] { new KeyValuePair<string, string>("Accept", "application/json") };

    /// <summary>Initializes a new instance of the <see cref="GoDaddyProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public GoDaddyProvider(IHttpClientFactory httpClientFactory, ILogger<GoDaddyProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.GoDaddy;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Zone))
        {
            return DnsUpdateResult.Fail("GoDaddy requires a hostname and a zone (domain).");
        }

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("GoDaddy requires an API key (login) and secret (password). See https://developer.godaddy.com/keys/.");
        }

        var zone = record.Zone.Trim();
        var hostname = record.Hostname.Trim();
        if (hostname.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
        {
            hostname = hostname.Substring(0, hostname.Length - zone.Length - 1);
        }

        var server = ServerBase(record, DefaultServer);
        var authorization = string.Concat("sso-key ", record.Login, ":", record.Password);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, zone, hostname, type, address, record.Ttl, authorization, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string zone,
        string hostname,
        string type,
        string value,
        int ttl,
        string authorization,
        CancellationToken cancellationToken)
    {
        var url = string.Concat(server, "/", Uri.EscapeDataString(zone), "/records/", type, "/", Uri.EscapeDataString(hostname));
        var headers = new List<KeyValuePair<string, string>>(AcceptHeader) { new("Authorization", authorization) };

        var result = await SendAsync(HttpMethod.Put, url, cancellationToken, headers, BuildBody(value, hostname, type, ttl)).ConfigureAwait(false);

        return result.Ok
            ? (true, "set to " + value)
            : (false, string.Concat(DescribeError(result.Status), " (HTTP ", result.Status.ToString(CultureInfo.InvariantCulture), ")"));
    }

    private static string BuildBody(string value, string hostname, string type, int ttl)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("data", value);
            if (ttl > 0)
            {
                writer.WriteNumber("ttl", ttl);
            }

            writer.WriteString("name", hostname);
            writer.WriteString("type", type);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string DescribeError(int code) => code switch
    {
        400 => "GoDaddy API URL was malformed.",
        401 => "login or password incorrect or missing. See https://developer.godaddy.com/keys/.",
        403 => "permission denied for the supplied login and password.",
        404 => "host not found at GoDaddy; check the zone option and login/password.",
        422 => "invalid domain or missing A/AAAA record.",
        429 => "too many requests to GoDaddy within a brief period.",
        503 => "host is unavailable.",
        _ => "unexpected service response.",
    };
}
