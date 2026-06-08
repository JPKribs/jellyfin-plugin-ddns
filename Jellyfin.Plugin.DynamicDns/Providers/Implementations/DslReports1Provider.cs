using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// DSLReports (port of ddclient's <c>nic_dslreports1_update</c>). Single basic-auth GET whose body
/// must contain the return code NOERROR. Login/Password are the DSLReports credentials, Hostname is
/// the host id, and Server overrides the default endpoint.
/// </summary>
public sealed class DslReports1Provider : DNSProviderBase
{
    private const string DefaultServer = "www.dslreports.com";

    /// <summary>Initializes a new instance of the <see cref="DslReports1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DslReports1Provider(IHttpClientFactory httpClientFactory, ILogger<DslReports1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.DslReports1;

    /// <inheritdoc />
    public override string Label => "DSLReports";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your DSLReports credentials.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Host",
        Login = "Username",
        Password = "Password",
        Server = true,
        Advanced = new[] { "static" },
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
            return DNSUpdateResult.Fail("A hostname (host id) is required.");
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

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        return result.Body.Contains("NOERROR", StringComparison.OrdinalIgnoreCase)
            ? DNSUpdateResult.Ok("IP address set to " + value)
            : DNSUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
