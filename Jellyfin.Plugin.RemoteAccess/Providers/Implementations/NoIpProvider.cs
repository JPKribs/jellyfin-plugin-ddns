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
/// No-IP, ported from ddclient's <c>nic_noip_update</c>: a single basic-auth GET to the update endpoint
/// carrying both addresses. Login/Password are the No-IP credentials, Hostname is the host to update, and
/// Server optionally overrides the default update endpoint.
/// </summary>
public sealed class NoIpProvider : DNSProviderBase
{
    private const string DefaultServer = "dynupdate.no-ip.com";

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["badauth"] = "Invalid username or password",
        ["badagent"] = "Invalid user agent",
        ["nohost"] = "The hostname does not exist in the database",
        ["!donator"] = "The offline setting requires a donator account",
        ["abuse"] = "The hostname is blocked for abuse",
        ["numhost"] = "Too many or too few hosts found",
        ["dnserr"] = "System DNS error encountered",
        ["nochg"] = "No update required (current address already set)"
    };

    /// <summary>Initializes a new instance of the <see cref="NoIpProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public NoIpProvider(IHttpClientFactory httpClientFactory, ILogger<NoIpProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.NoIp;

    /// <inheritdoc />
    public override string Label => "No-IP";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your No-IP credentials. A DDNS key is recommended over your account password.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "Password or DDNS key",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("No-IP requires a login and password.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var addresses = new List<string>();
        if (record.UpdateIPv4 && ip.IPv4 is not null)
        {
            addresses.Add(ip.IPv4);
        }

        if (record.UpdateIPv6 && ip.IPv6 is not null)
        {
            addresses.Add(ip.IPv6);
        }

        if (addresses.Count == 0)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var url = ServerBase(record, DefaultServer)
            + "/nic/update?system=noip&hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(string.Join(",", addresses));

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var status = FirstToken(result.Body).ToLowerInvariant();
        if (status is "good" or "nochg")
        {
            return DNSUpdateResult.Ok(status);
        }

        if (Errors.TryGetValue(status, out var msg))
        {
            return DNSUpdateResult.Fail(status + ": " + msg);
        }

        return DNSUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
