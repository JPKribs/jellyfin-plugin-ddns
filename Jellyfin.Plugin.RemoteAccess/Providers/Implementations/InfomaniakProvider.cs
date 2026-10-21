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
/// Infomaniak (port of ddclient's <c>nic_infomaniak_update</c>). Basic-auth GET to <c>/nic/update</c>
/// with the hostname and IP. Both A and AAAA use the <c>myip</c> parameter, one request per record type.
/// <see cref="DNSRecord.Login"/>/<see cref="DNSRecord.Password"/> are the credentials,
/// <see cref="DNSRecord.Hostname"/> the record, and <see cref="DNSRecord.Server"/> overrides the endpoint.
/// </summary>
public sealed class InfomaniakProvider : DNSProviderBase
{
    private const string DefaultServer = "infomaniak.com";

    private static readonly Dictionary<string, string> Statuses = new(StringComparer.Ordinal)
    {
        ["good"] = "IP set",
        ["nochg"] = "IP already set",
        ["nohost"] = "Bad domain name or bad IP",
        ["badauth"] = "Bad authentication"
    };

    /// <summary>Initializes a new instance of the <see cref="InfomaniakProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public InfomaniakProvider(IHttpClientFactory httpClientFactory, ILogger<InfomaniakProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Infomaniak;

    /// <inheritdoc />
    public override string Label => "Infomaniak";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your Infomaniak credentials.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
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

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateOneAsync(server, record.Hostname, record.Login, record.Password, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateOneAsync(string server, string hostname, string login, string password, string value, CancellationToken cancellationToken)
    {
        var url = server
            + "/nic/update?hostname=" + Uri.EscapeDataString(hostname)
            + "&myip=" + Uri.EscapeDataString(value);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: login, password: password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        var status = FirstToken(result.Body);
        if (string.Equals(status, "good", StringComparison.Ordinal) || string.Equals(status, "nochg", StringComparison.Ordinal))
        {
            return (true, status);
        }

        if (Statuses.TryGetValue(status, out var msg))
        {
            return (false, status + " (" + msg + ")");
        }

        return (false, "unknown reply: " + FirstLine(result.Body));
    }
}
