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
/// DynDNS v2 protocol (port of ddclient's <c>nic_dyndns2_update</c>). Login/Password are basic-auth
/// credentials. Hostname is the FQDN. Server overrides the default members.dyndns.org. Sends both
/// addresses in one request.
/// </summary>
public sealed class DynDns2Provider : DNSProviderBase
{
    private const string DefaultServer = "members.dyndns.org";
    private const string Script = "/nic/update";

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["badauth"] = "Bad authorization (username or password)",
        ["badsys"] = "The system parameter given was not valid",
        ["notfqdn"] = "A fully-qualified domain name was not provided",
        ["nohost"] = "The hostname does not exist in the database",
        ["!yours"] = "The hostname exists, but not under this username",
        ["!donator"] = "The offline setting requires a donator account",
        ["!active"] = "The hostname is in a Custom DNS domain not yet activated",
        ["abuse"] = "The hostname is blocked for abuse",
        ["numhost"] = "Too many or too few hosts found",
        ["dnserr"] = "System DNS error encountered",
        ["nochg"] = "No update required (current address already set)"
    };

    /// <summary>Initializes a new instance of the <see cref="DynDns2Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DynDns2Provider(IHttpClientFactory httpClientFactory, ILogger<DynDns2Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.DynDns2;

    /// <inheritdoc />
    public override string Label => "DynDNS v2";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your account credentials. Server defaults to members.dyndns.org. Override it for a compatible service.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "Password",
        Server = true,
        Advanced = new[] { "wildcard", "mx", "backupmx" },
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

        var url = ServerBase(record, DefaultServer) + Script
            + "?hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(string.Join(",", addresses));

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
            return DNSUpdateResult.Fail("HTTP " + result.Status + ": " + FirstLine(result.Body));
        }

        return Interpret(result.Body);
    }

    /// <summary>Maps a dyndns2 response body to a result (first token is the status code).</summary>
    private static DNSUpdateResult Interpret(string body)
    {
        var status = FirstToken(body);
        if (string.Equals(status, "good", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "nochg", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Ok(status);
        }

        return DNSUpdateResult.Fail(Errors.TryGetValue(status, out var msg) ? status + ": " + msg : "server said: " + FirstLine(body));
    }
}
