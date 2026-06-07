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
/// Hetzner Cloud DNS API. Port of ddclient's <c>nic_hetzner_update</c>: for each enabled record type it
/// looks up the existing rrset, then either sets its records or creates a new rrset, and polls the
/// returned action until it reports success. Set password to a Hetzner Cloud API token; login is unused.
/// </summary>
public sealed class HetznerProvider : DnsProviderBase
{
    private const string DefaultServer = "api.hetzner.cloud/v1";
    private const int MaxStatusTries = 5;

    /// <summary>Initializes a new instance of the <see cref="HetznerProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public HetznerProvider(IHttpClientFactory httpClientFactory, ILogger<HetznerProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Hetzner;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Zone))
        {
            return DnsUpdateResult.Fail("Hetzner requires a DNS zone.");
        }

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Hetzner requires an API token (password).");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("Hetzner requires a hostname.");
        }

        var zone = record.Zone.Trim();
        var hostname = ComputeHostname(record.Hostname.Trim(), zone);
        var server = ServerBase(record, DefaultServer);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Authorization", "Bearer " + record.Password)
        };

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateTypeAsync(server, zone, hostname, type, address, record.Ttl, headers, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeHostname(string fqdn, string zone)
    {
        var suffix = "." + zone;
        if (fqdn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var host = fqdn.Substring(0, fqdn.Length - suffix.Length);
            return string.IsNullOrEmpty(host) ? "@" : host;
        }

        // ddclient leaves $hostname == $domain when the zone is not a suffix; it then maps the
        // exact-zone case to '@'.
        return string.Equals(fqdn, zone, StringComparison.OrdinalIgnoreCase) ? "@" : fqdn;
    }

    private async Task<(bool Ok, string Message)> UpdateTypeAsync(
        string server,
        string zone,
        string hostname,
        string type,
        string ipValue,
        int ttl,
        List<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var encodedHost = Uri.EscapeDataString(hostname);
        var rrsetUrl = server + "/zones/" + Uri.EscapeDataString(zone) + "/rrsets/" + encodedHost + "/" + type;

        // A 404 lookup means the rrset does not exist yet and must be created rather than set.
        var lookup = await SendAsync(HttpMethod.Get, rrsetUrl, cancellationToken, headers).ConfigureAwait(false);
        bool exists;
        if (lookup.Status == 404)
        {
            exists = false;
        }
        else if (!lookup.Ok)
        {
            return (false, "lookup failed (HTTP " + lookup.Status + ")");
        }
        else
        {
            exists = HasRrset(lookup.Body);
        }

        string actionUrl;
        string body;
        if (exists)
        {
            actionUrl = rrsetUrl + "/actions/set_records";
            body = "{\"records\": [{\"value\": \"" + ipValue + "\"}]}";
        }
        else
        {
            actionUrl = server + "/zones/" + Uri.EscapeDataString(zone) + "/rrsets";
            // hostname is user-supplied; JSON-encode it so a name with a quote/backslash can't corrupt the body.
            body = "{\"name\":" + JsonSerializer.Serialize(hostname) + ",\"type\":\"" + type + "\",\"ttl\":" + ttl.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"records\":[{\"value\":\"" + ipValue + "\"}]}";
        }

        var apply = await SendAsync(HttpMethod.Post, actionUrl, cancellationToken, headers, body).ConfigureAwait(false);
        if (!apply.Ok)
        {
            return (false, "update failed (HTTP " + apply.Status + ")");
        }

        var actionId = ReadActionId(apply.Body);
        return actionId is null
            ? (false, "update failed (invalid json or result)")
            : await PollActionAsync(server, ipValue, actionId, headers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PollActionAsync(
        string server,
        string ipValue,
        string actionId,
        List<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var statusUrl = server + "/actions/" + Uri.EscapeDataString(actionId);
        for (var tryNumber = 1; tryNumber <= MaxStatusTries; tryNumber++)
        {
            var check = await SendAsync(HttpMethod.Get, statusUrl, cancellationToken, headers).ConfigureAwait(false);
            if (check.Ok)
            {
                var (status, message) = ReadActionStatus(check.Body);
                if (string.Equals(status, "success", StringComparison.Ordinal))
                {
                    return (true, "set to " + ipValue);
                }

                if (string.Equals(status, "error", StringComparison.Ordinal))
                {
                    return (false, "update failed (" + (message ?? "unknown error") + ")");
                }
            }

            if (tryNumber == MaxStatusTries)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }

        return (false, "update failed (timeout while checking action " + actionId + ")");
    }

    private static bool HasRrset(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("rrset", out var rrset) && rrset.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadActionId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!action.TryGetProperty("id", out var id))
            {
                return null;
            }

            return id.ValueKind == JsonValueKind.Number
                ? id.GetRawText()
                : id.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Status, string? Message) ReadActionStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? status = null;
            if (action.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            {
                status = statusElement.GetString();
            }

            string? message = null;
            if (action.TryGetProperty("message", out var actionMessage) && actionMessage.ValueKind == JsonValueKind.String)
            {
                message = actionMessage.GetString();
            }
            else if (action.TryGetProperty("error", out var actionError)
                && actionError.ValueKind == JsonValueKind.Object
                && actionError.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                message = errorMessage.GetString();
            }
            else if (doc.RootElement.TryGetProperty("error", out var rootError)
                && rootError.ValueKind == JsonValueKind.Object
                && rootError.TryGetProperty("message", out var rootErrorMessage)
                && rootErrorMessage.ValueKind == JsonValueKind.String)
            {
                message = rootErrorMessage.GetString();
            }

            return (status, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
