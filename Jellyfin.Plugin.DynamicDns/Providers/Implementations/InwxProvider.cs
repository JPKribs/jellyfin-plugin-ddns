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
/// INWX DynDNS (port of ddclient's <c>nic_inwx_update</c>). Like dyndns2 but IPv6 is passed in a separate
/// <c>myipv6</c> parameter in the same request. Login/Password are the INWX basic-auth credentials. The
/// hostname is derived by INWX from those credentials, not sent. Server overrides the default endpoint.
/// </summary>
public sealed class InwxProvider : DNSProviderBase
{
    private const string DefaultServer = "dyndns.inwx.com";
    private const string Script = "/nic/update";

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["badauth"] = "Bad authorization (username or password)",
        ["badsys"] = "The system parameter given was not valid",
        ["notfqdn"] = "A Fully-Qualified Domain Name was not provided",
        ["nohost"] = "The hostname specified does not exist in the database",
        ["!yours"] = "The hostname specified exists, but not under the username currently being used",
        ["!donator"] = "The offline setting was set, when the user is not a donator",
        ["!active"] = "The hostname specified is in a Custom DNS domain which has not yet been activated.",
        ["abuse"] = "The hostname specified is blocked for abuse",
        ["numhost"] = "System error: Too many or too few hosts found.",
        ["dnserr"] = "System error: DNS error encountered.",
        ["nochg"] = "No update required; the current address is already set"
    };

    /// <summary>Initializes a new instance of the <see cref="InwxProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public InwxProvider(IHttpClientFactory httpClientFactory, ILogger<InwxProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Inwx;

    /// <inheritdoc />
    public override string Label => "INWX";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your INWX DynDNS account. The host is derived from the account.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Login = "Username",
        Password = "Password",
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

        // INWX requires the IPv4 A record to be updated at the same time as the IPv6 AAAA record.
        if (ipv6 is not null && ipv4 is null)
        {
            return DNSUpdateResult.Fail(
                "INWX requires the IPv4 address to be updated alongside IPv6, but no IPv4 address is available.");
        }

        var url = ServerBase(record, DefaultServer) + Script + "?";
        if (ipv4 is not null)
        {
            url += "myip=" + Uri.EscapeDataString(ipv4);
        }

        if (ipv6 is not null)
        {
            if (ipv4 is not null)
            {
                url += "&";
            }

            url += "myipv6=" + Uri.EscapeDataString(ipv6);
        }

        var result = await SendAsync(
            HttpMethod.Get,
            url,
            cancellationToken,
            login: record.Login,
            password: record.Password).ConfigureAwait(false);

        // INWX can return 200 OK even on error, so the body must be inspected for the status token.
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        var status = FirstToken(result.Body);
        if (string.IsNullOrEmpty(status))
        {
            return DNSUpdateResult.Fail("Could not connect to " + DefaultServer + " (empty response).");
        }

        if (string.Equals(status, "good", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "nochg", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Ok(status);
        }

        return DNSUpdateResult.Fail(
            Errors.TryGetValue(status, out var msg)
                ? status + ": " + msg
                : "unexpected status: " + FirstLine(result.Body));
    }
}
