using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Legacy DynDNS v1 protocol (port of ddclient's <c>nic_dyndns1_update</c>). Sends one basic-auth GET
/// covering a single address and treats a "return code: NOERROR" body as success.
/// Login/Password are the account credentials, Hostname is the host to update, and Server overrides the
/// update endpoint (default <c>members.dyndns.org</c>).
/// </summary>
public sealed class DynDns1Provider : DNSProviderBase
{
    private const string DefaultServer = "members.dyndns.org";

    /// <summary>Initializes a new instance of the <see cref="DynDns1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DynDns1Provider(IHttpClientFactory httpClientFactory, ILogger<DynDns1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.DynDns1;

    /// <inheritdoc />
    public override string Label => "DynDNS v1 (legacy)";

    /// <inheritdoc />
    public override string Hint => "Legacy DynDNS v1. Login and Password are your credentials.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "Password",
        Server = true,
        Advanced = new[] { "wildcard", "static", "mx", "backupmx" },
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

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var value = (record.UpdateIPv4 ? ip.IPv4 : null) ?? (record.UpdateIPv6 ? ip.IPv6 : null);
        if (value is null)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var service = record.Static ? "statdns" : "dyndns";
        var url = ServerBase(record, DefaultServer) + "/nic/" + service
            + "?action=edit&started=1&hostname=YES&host_id=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(value);
        if (record.Wildcard)
        {
            url += "&wildcard=ON";
        }

        if (!string.IsNullOrWhiteSpace(record.Mx))
        {
            url += "&mx=" + Uri.EscapeDataString(record.Mx) + "&backmx=" + (record.BackupMx ? "YES" : "NO");
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var body = result.Body;
        var ok = body.Contains("return code", StringComparison.OrdinalIgnoreCase)
            && body.Contains("NOERROR", StringComparison.OrdinalIgnoreCase)
            && !body.Contains("error code: ABUSE", StringComparison.OrdinalIgnoreCase);

        return ok
            ? DNSUpdateResult.Ok("IP address set to " + value)
            : DNSUpdateResult.Fail("server said: " + FirstLine(body));
    }
}
