using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Dynadot (port of ddclient's <c>nic_dynadot_update</c>). Password holds the DDNS password. The
/// zone, if set, splits the hostname into domain + subdomain.
/// </summary>
public sealed class DynadotProvider : DNSProviderBase
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
    public override DNSProviderKind Kind => DNSProviderKind.Dynadot;

    /// <inheritdoc />
    public override string Label => "Dynadot";

    /// <inheritdoc />
    public override string Hint => "Password is your DDNS password. Zone is optional and splits the hostname into domain and subdomain.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Password = "DDNS password",
        Zone = "Domain",
        Server = true,
        Ttl = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("Dynadot requires a hostname and a DDNS password.");
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
                return DNSUpdateResult.Fail("hostname does not end with the zone value: " + domain);
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
            (type, address, ct) => PushAsync(server, record.Password, record.Ttl > 1 ? record.Ttl : 300, domain, subDomain, isRoot, containRoot, type, address, ct),
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

        // Dynadot's set_ddns returns the bare token "ok" on success. Compare the first token rather than
        // searching the whole body, where "ok" would match words like "token" or "lookup".
        var firstLine = FirstLine(result.Body);
        return string.Equals(FirstToken(result.Body), "ok", StringComparison.OrdinalIgnoreCase)
            ? (true, "set to " + value)
            : (false, "failed (" + firstLine + ")");
    }
}
