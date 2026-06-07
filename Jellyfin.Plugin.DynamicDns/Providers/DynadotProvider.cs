using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Dynadot (port of ddclient's <c>nic_dynadot_update</c>). Password holds the DDNS password; the
/// zone, if set, splits the hostname into domain + subdomain.
/// </summary>
public sealed class DynadotProvider : DnsProviderBase
{
    private const string DefaultServer = "www.dynadot.com";

    /// <summary>Initializes a new instance of the <see cref="DynadotProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DynadotProvider(IHttpClientFactory httpClientFactory, ILogger<DynadotProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Dynadot;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Dynadot requires a hostname and a DDNS password.");
        }

        string domain;
        string subDomain;
        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            domain = record.Zone;
            if (string.Equals(record.Hostname, domain, StringComparison.OrdinalIgnoreCase))
            {
                subDomain = string.Empty;
            }
            else if (record.Hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                subDomain = record.Hostname.Substring(0, record.Hostname.Length - domain.Length - 1);
            }
            else
            {
                return DnsUpdateResult.Fail("hostname does not end with the zone value: " + domain);
            }
        }
        else
        {
            var dot = record.Hostname.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0)
            {
                subDomain = record.Hostname.Substring(0, dot);
                domain = record.Hostname.Substring(dot + 1);
            }
            else
            {
                subDomain = string.Empty;
                domain = record.Hostname;
            }
        }

        var isRoot = subDomain.Length == 0;
        var containRoot = isRoot ? "true" : "false";
        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record.Password, record.Ttl, domain, subDomain, isRoot, containRoot, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string password,
        int ttl,
        string domain,
        string subDomain,
        bool isRoot,
        string containRoot,
        string type,
        string value,
        CancellationToken cancellationToken)
    {
        var url = server + "/set_ddns?containRoot=" + containRoot
            + "&domain=" + Uri.EscapeDataString(domain)
            + "&ip=" + Uri.EscapeDataString(value)
            + "&pwd=" + Uri.EscapeDataString(password)
            + "&ttl=" + ttl
            + "&type=" + type;
        if (!isRoot)
        {
            url += "&subDomain=" + Uri.EscapeDataString(subDomain);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        return result.Body.Contains("ok", StringComparison.OrdinalIgnoreCase)
            ? (true, "set to " + value)
            : (false, "failed (" + FirstLine(result.Body) + ")");
    }
}
