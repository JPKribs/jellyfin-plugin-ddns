using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Mythic Beasts Dynamic DNS (port of ddclient's <c>nic_mythicdyn_update</c>). Put the API key in
/// <see cref="DNSRecord.Login"/> and the secret in <see cref="DNSRecord.Password"/> (basic auth), and
/// the record name in <see cref="DNSRecord.Hostname"/>. <see cref="DNSRecord.Server"/> optionally
/// overrides the default API host. Each enabled IP version is POSTed independently. The service sets
/// the record to the request's source address, so the detected IP only decides whether to update.
/// </summary>
public sealed class MythicDynProvider : DNSProviderBase
{
    private const string DefaultServer = "api.mythic-beasts.com";

    /// <summary>Initializes a new instance of the <see cref="MythicDynProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public MythicDynProvider(IHttpClientFactory httpClientFactory, ILogger<MythicDynProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.MythicDyn;

    /// <inheritdoc />
    public override string Label => "Mythic Beasts";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your Mythic Beasts dynamic DNS credentials.";

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

        // ddclient prefixes the host with "ipv4."/"ipv6." so the request is forced over the matching
        // transport. Split the scheme off the base so the per-family helper can splice the prefix in.
        var baseHost = ServerBase(record, DefaultServer);
        var schemeEnd = baseHost.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "https://" : baseHost.Substring(0, schemeEnd + 3);
        var host = schemeEnd < 0 ? baseHost : baseHost.Substring(schemeEnd + 3);
        var path = "/dns/v2/dynamic/" + Uri.EscapeDataString(record.Hostname);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(scheme, host, path, type, record.Login, record.Password, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string scheme,
        string host,
        string path,
        string type,
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        var prefix = string.Equals(type, "AAAA", StringComparison.Ordinal) ? "ipv6." : "ipv4.";
        var url = string.Concat(scheme, prefix, host, path);

        var result = await SendAsync(HttpMethod.Post, url, cancellationToken, login: login, password: password).ConfigureAwait(false);

        return result.Ok
            ? (true, "updated successfully")
            : (false, "HTTP " + result.Status + " " + FirstLine(result.Body));
    }
}
