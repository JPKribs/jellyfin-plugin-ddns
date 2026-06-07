using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Namecheap (port of ddclient's <c>nic_namecheap_update</c>). <see cref="DnsRecord.Login"/> holds the
/// domain (e.g. <c>example.com</c>) and <see cref="DnsRecord.Password"/> holds the domain's Dynamic DNS
/// password; the host label is derived by stripping the domain suffix from the hostname.
/// </summary>
public sealed class NamecheapProvider : DnsProviderBase
{
    private const string DefaultServer = "dynamicdns.park-your-domain.com";

    /// <summary>Initializes a new instance of the <see cref="NamecheapProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public NamecheapProvider(IHttpClientFactory httpClientFactory, ILogger<NamecheapProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Namecheap;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname)
            || string.IsNullOrWhiteSpace(record.Login)
            || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Namecheap requires a hostname, login (domain), and password (DDNS password).");
        }

        var domain = record.Login.Trim();

        // ddclient: $host =~ s/(.*)\.$domain(.*)/$1$2/  — strip the ".domain" suffix from the hostname.
        var host = record.Hostname.Trim();
        var suffix = "." + domain;
        if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            host = host.Substring(0, host.Length - suffix.Length);
        }
        else if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
        {
            host = string.Empty;
        }

        // Namecheap treats an empty host as the root record (@).
        if (host.Length == 0)
        {
            host = "@";
        }

        var server = ServerBase(record, DefaultServer);
        var password = record.Password;

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, host, domain, password, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string host,
        string domain,
        string password,
        string value,
        CancellationToken cancellationToken)
    {
        var url = server + "/update?host=" + Uri.EscapeDataString(host)
            + "&domain=" + Uri.EscapeDataString(domain)
            + "&password=" + Uri.EscapeDataString(password)
            + "&ip=" + Uri.EscapeDataString(value);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        // ddclient success detection: reply contains <ErrCount>0 (case-insensitive).
        return result.Body.Contains("<ErrCount>0", StringComparison.OrdinalIgnoreCase)
            ? (true, "set to " + value)
            : (false, "failed (" + FirstLine(result.Body) + ")");
    }
}
