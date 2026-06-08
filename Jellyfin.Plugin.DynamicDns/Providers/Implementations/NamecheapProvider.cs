using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Namecheap (port of ddclient's <c>nic_namecheap_update</c>). <see cref="DNSRecord.Login"/> holds the
/// domain (e.g. <c>example.com</c>) and <see cref="DNSRecord.Password"/> holds the domain's Dynamic DNS
/// password. The host label is derived by stripping the domain suffix from the hostname.
/// </summary>
public sealed class NamecheapProvider : DNSProviderBase
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
    public override DNSProviderKind Kind => DNSProviderKind.Namecheap;

    /// <inheritdoc />
    public override string Label => "Namecheap";

    /// <inheritdoc />
    public override string Hint => "Login is the domain. Password is the domain DDNS password. Hostname is host.domain.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Host and domain",
        Login = "Domain",
        Password = "DDNS password",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname)
            || string.IsNullOrWhiteSpace(record.Login)
            || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("Namecheap requires a hostname, login (domain), and password (DDNS password).");
        }

        var domain = record.Login.Trim();

        // ddclient: $host =~ s/(.*)\.$domain(.*)/$1$2/ strips the ".domain" suffix from the hostname.
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
