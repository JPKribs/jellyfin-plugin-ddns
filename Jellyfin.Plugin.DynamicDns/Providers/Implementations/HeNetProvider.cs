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
/// Hurricane Electric / dns.he.net (port of ddclient's <c>nic_henet_update</c>). Basic-auth GET to
/// <c>/nic/update</c> where the auth user is <see cref="DNSRecord.Hostname"/> and the auth password is
/// the service-generated key in <see cref="DNSRecord.Password"/>. <see cref="DNSRecord.Server"/> may
/// override the default host. IPv4 and IPv6 are updated in separate calls.
/// </summary>
public sealed class HeNetProvider : DNSProviderBase
{
    private const string DefaultServer = "dyn.dns.he.net";

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["badauth"] = "Bad authorization (username or password)",
        ["badsys"] = "The system parameter given was not valid",
        ["nohost"] = "The hostname specified does not exist in the database",
        ["abuse"] = "The hostname specified is blocked for abuse",
        ["nochg"] = "No update required; unnecessary attempts to change the current address are considered abusive"
    };

    /// <summary>Initializes a new instance of the <see cref="HeNetProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public HeNetProvider(IHttpClientFactory httpClientFactory, ILogger<HeNetProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.HeNet;

    /// <inheritdoc />
    public override string Label => "Hurricane Electric";

    /// <inheritdoc />
    public override string Hint => "Hostname is the record. Password is the per record DDNS key. Login is unused.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record",
        Password = "DDNS key",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A password is required.");
        }

        var serverBase = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(serverBase, record.Hostname, record.Password, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string serverBase,
        string hostname,
        string password,
        string ipValue,
        CancellationToken cancellationToken)
    {
        var url = serverBase
            + "/nic/update?hostname=" + Uri.EscapeDataString(hostname)
            + "&myip=" + Uri.EscapeDataString(ipValue);

        // ddclient authenticates with login => hostname, password => opt('password').
        var result = await SendAsync(
            HttpMethod.Get,
            url,
            cancellationToken,
            login: hostname,
            password: password).ConfigureAwait(false);

        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        // The service can return 200 OK even on error, so the body must be parsed.
        var line = FirstLine(result.Body);
        var status = FirstToken(line).ToLowerInvariant();
        string? returnedIp = null;
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space >= 0)
        {
            returnedIp = line[(space + 1)..].Trim();
        }

        // "nochg" means the address already matched, which is a success for our purposes.
        if (string.Equals(status, "nochg", StringComparison.Ordinal))
        {
            status = "good";
        }

        if (!string.Equals(status, "good", StringComparison.Ordinal))
        {
            var detail = Errors.TryGetValue(status, out var msg)
                ? status + ": " + msg
                : "unexpected status: " + line;
            return (false, detail);
        }

        var setTo = string.IsNullOrEmpty(returnedIp) ? ipValue : returnedIp;
        return (true, status + " (set to " + setTo + ")");
    }
}
