using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Dynu (port of ddclient's <c>nic_dynu_update</c>). A single basic-auth GET carries both addresses.
/// Login/Password are the Dynu credentials. Server overrides the API host. Hostname is the FQDN. Zone,
/// if set, is the apex domain and splits Hostname into hostname=zone + alias=subdomain.
/// </summary>
public sealed class DynuProvider : DNSProviderBase
{
    private const string DefaultServer = "api.dynu.com";

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["badauth"] = "Bad authorization (username or password)",
        ["notfqdn"] = "A fully-qualified domain name was not provided",
        ["nohost"] = "The hostname does not exist",
        ["!donator"] = "Feature restricted to members",
        ["numhost"] = "Too many hostnames specified",
        ["abuse"] = "Update blocked due to abusive behaviour",
        ["servererror"] = "Server error; retry later",
        ["dnserr"] = "DNS error encountered; retry later",
        ["911"] = "Server maintenance; retry in 10 minutes",
        ["nochg"] = "IP address is current; no update required"
    };

    /// <summary>Initializes a new instance of the <see cref="DynuProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DynuProvider(IHttpClientFactory httpClientFactory, ILogger<DynuProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Dynu;

    /// <inheritdoc />
    public override string Label => "Dynu";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your Dynu credentials. Set Zone for a custom domain to split the hostname into zone and alias.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "Password",
        Zone = "Custom domain",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A login and password are required.");
        }

        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        var ipv6 = record.UpdateIPv6 ? ip.IPv6 : null;
        if (ipv4 is null && ipv6 is null)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        string hostname;
        string? alias = null;
        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            if (string.Equals(record.Hostname, record.Zone, StringComparison.OrdinalIgnoreCase))
            {
                alias = string.Empty;
            }
            else if (record.Hostname.EndsWith("." + record.Zone, StringComparison.OrdinalIgnoreCase))
            {
                alias = record.Hostname.Substring(0, record.Hostname.Length - record.Zone.Length - 1);
            }
            else
            {
                return DNSUpdateResult.Fail("hostname does not end with the zone: " + record.Zone);
            }

            hostname = record.Zone;
        }
        else
        {
            hostname = record.Hostname;
        }

        var url = ServerBase(record, DefaultServer) + "/nic/update?hostname=" + Uri.EscapeDataString(hostname);
        if (!string.IsNullOrEmpty(alias))
        {
            url += "&alias=" + Uri.EscapeDataString(alias);
        }

        if (ipv4 is not null)
        {
            url += "&myip=" + Uri.EscapeDataString(ipv4);
        }
        else if (ipv6 is not null)
        {
            // ddclient quirk: when only IPv6 is updated, myip=no leaves the existing A record untouched.
            url += "&myip=no";
        }

        if (ipv6 is not null)
        {
            url += "&myipv6=" + Uri.EscapeDataString(ipv6);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var status = FirstToken(result.Body);
        if (string.Equals(status, "good", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "nochg", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Ok(status);
        }

        return DNSUpdateResult.Fail(Errors.TryGetValue(status, out var msg) ? status + ": " + msg : "server said: " + FirstLine(result.Body));
    }
}
