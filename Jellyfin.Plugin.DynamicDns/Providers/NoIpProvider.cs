using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// No-IP, ported from ddclient's <c>nic_noip_update</c>: a single basic-auth GET to the update endpoint
/// carrying both addresses. Login/Password are the No-IP credentials, Hostname is the host to update, and
/// Server optionally overrides the default update endpoint.
/// </summary>
public sealed class NoIpProvider : DnsProviderBase
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
    public override DnsProviderKind Kind => DnsProviderKind.NoIp;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("No-IP requires a login and password.");
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
            return DnsUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var url = ServerBase(record, DefaultServer)
            + "/nic/update?system=noip&hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(string.Join(",", addresses));

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DnsUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var status = FirstToken(result.Body).ToLowerInvariant();
        if (status is "good" or "nochg")
        {
            return DnsUpdateResult.Ok(status);
        }

        if (Errors.TryGetValue(status, out var msg))
        {
            return DnsUpdateResult.Fail(status + ": " + msg);
        }

        return DnsUpdateResult.Fail("server said: " + FirstLine(result.Body));
    }
}
