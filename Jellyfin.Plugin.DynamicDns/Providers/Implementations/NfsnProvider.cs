using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// NearlyFreeSpeech.NET (port of ddclient's <c>nic_nfsn_update</c>). Each request carries a custom
/// <c>X-NFSN-Authentication</c> header (login;timestamp;salt;SHA1-hash) and uses form-urlencoded
/// POSTs under <c>/dns/{zone}/</c>. There is no "updateRR" call: an existing A record is removed
/// (removeRR) and re-added (addRR). ddclient only manages the A (IPv4) record, so this provider does too.
/// <para>Credentials: <c>Login</c> = NFSN member login, <c>Password</c> = API key, <c>Zone</c> = DNS zone.</para>
/// </summary>
public sealed class NfsnProvider : DNSProviderBase
{
    private const string DefaultServer = "api.nearlyfreespeech.net";
    private const string SaltChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly KeyValuePair<string, string>[] FormContentType =
    {
        new("Content-Type", "application/x-www-form-urlencoded")
    };

    /// <summary>Initializes a new instance of the <see cref="NfsnProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public NfsnProvider(IHttpClientFactory httpClientFactory, ILogger<NfsnProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Nfsn;

    /// <inheritdoc />
    public override string Label => "NearlyFreeSpeech.NET";

    /// <inheritdoc />
    public override string Hint => "Login is the member login. Password is the API key. Zone is the DNS zone.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Member login",
        Password = "API key",
        Zone = "DNS zone",
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
            return DNSUpdateResult.Fail("A login (member login) and password (API key) are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Zone))
        {
            return DNSUpdateResult.Fail("A zone is required.");
        }

        // ddclient's nfsn protocol only manages the A (IPv4) record.
        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        if (string.IsNullOrWhiteSpace(ipv4))
        {
            return DNSUpdateResult.Fail("No IPv4 address detected (nfsn only updates A records).");
        }

        var zone = record.Zone.Trim();
        var host = record.Hostname.Trim();

        string name;
        if (string.Equals(host, zone, StringComparison.OrdinalIgnoreCase))
        {
            name = string.Empty;
        }
        else if (host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
        {
            name = host.Substring(0, host.Length - zone.Length - 1);
        }
        else
        {
            return DNSUpdateResult.Fail(host + " is outside zone " + zone);
        }

        var baseUrl = ServerBase(record, DefaultServer);

        // The zone becomes a URL path segment, and that same path is hashed into the X-NFSN-Authentication
        // header, so it must be escaped once and reused for both to keep the request and signature aligned.
        var zoneSegment = Uri.EscapeDataString(zone);

        // 1. List existing A records for this name so any current value can be removed first.
        var listPath = "/dns/" + zoneSegment + "/listRRs";
        var listBody = FormEncode(new[]
        {
            new KeyValuePair<string, string>("name", name),
            new KeyValuePair<string, string>("type", "A")
        });

        var listResp = await MakeRequestAsync(record, baseUrl, listPath, listBody, cancellationToken).ConfigureAwait(false);
        if (!listResp.Ok)
        {
            return DNSUpdateResult.Fail("listRRs failed: " + DescribeError(listResp));
        }

        string? existingData;
        try
        {
            existingData = FirstRecordData(listResp.Body);
        }
        catch (JsonException)
        {
            return DNSUpdateResult.Fail("JSON decoding failure on listRRs response.");
        }

        // 2. Remove the existing A record if one exists (there is no updateRR endpoint).
        if (existingData is not null)
        {
            var rmPath = "/dns/" + zoneSegment + "/removeRR";
            var rmBody = FormEncode(new[]
            {
                new KeyValuePair<string, string>("name", name),
                new KeyValuePair<string, string>("type", "A"),
                new KeyValuePair<string, string>("data", existingData)
            });

            var rmResp = await MakeRequestAsync(record, baseUrl, rmPath, rmBody, cancellationToken).ConfigureAwait(false);
            if (!rmResp.Ok)
            {
                return DNSUpdateResult.Fail("removeRR failed: " + DescribeError(rmResp));
            }
        }

        // 3. Add the A record with the new IP. ddclient defaults NFSN to a 300 second TTL, so use that
        // when the user left the 1 second sentinel.
        var ttl = record.Ttl > 1 ? record.Ttl : 300;
        var addPath = "/dns/" + zoneSegment + "/addRR";
        var addBody = FormEncode(new[]
        {
            new KeyValuePair<string, string>("name", name),
            new KeyValuePair<string, string>("type", "A"),
            new KeyValuePair<string, string>("data", ipv4),
            new KeyValuePair<string, string>("ttl", ttl.ToString(CultureInfo.InvariantCulture))
        });

        var addResp = await MakeRequestAsync(record, baseUrl, addPath, addBody, cancellationToken).ConfigureAwait(false);
        if (!addResp.Ok)
        {
            return DNSUpdateResult.Fail("addRR failed: " + DescribeError(addResp));
        }

        return DNSUpdateResult.Ok("A record set to " + ipv4);
    }

    private static string FormEncode(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(Uri.EscapeDataString(pair.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(pair.Value));
        }

        return sb.ToString();
    }

    private static string Sha1Hex(string value)
    {
        // SHA1 is mandated by the NFSN API authentication scheme. It is not used for security here.
#pragma warning disable CA5350
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
#pragma warning restore CA5350
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateSalt()
    {
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = SaltChars[RandomNumberGenerator.GetInt32(SaltChars.Length)];
        }

        return new string(chars);
    }

    private static string? FirstRecordData(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        if (first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.String)
        {
            return data.GetString();
        }

        return null;
    }

    private static string DescribeError(HttpResult resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var message = error.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("debug", out var debug)
                    && debug.ValueKind == JsonValueKind.String)
                {
                    var detail = debug.GetString();
                    if (!string.IsNullOrEmpty(detail))
                    {
                        message = message + " (" + detail + ")";
                    }
                }

                return message.Length > 0 ? message : "HTTP " + resp.Status;
            }
        }
        catch (JsonException)
        {
            // Body was not JSON, so fall through to the bare status code below.
        }

        return "HTTP " + resp.Status;
    }

    private async Task<HttpResult> MakeRequestAsync(
        DNSRecord record,
        string baseUrl,
        string path,
        string body,
        CancellationToken cancellationToken)
    {
        var url = baseUrl + path;
        var authHeader = BuildAuthHeader(record.Login, record.Password, path, body);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("X-NFSN-Authentication", authHeader)
        };
        if (body.Length > 0)
        {
            headers.AddRange(FormContentType);
        }

        return await SendAsync(
            HttpMethod.Post,
            url,
            cancellationToken,
            headers: headers,
            body: body,
            contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);
    }

    private static string BuildAuthHeader(string login, string apiKey, string path, string body)
    {
        // X-NFSN-Authentication: login;timestamp;salt;hash
        // hash = sha1_hex(login;timestamp;salt;api-key;request-uri;sha1_hex(body))
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var salt = GenerateSalt();
        var bodyHash = Sha1Hex(body);

        var hashString = string.Concat(
            login, ";", timestamp, ";", salt, ";", apiKey, ";", path, ";", bodyHash);
        var hash = Sha1Hex(hashString);

        return string.Concat(login, ";", timestamp, ";", salt, ";", hash);
    }
}
