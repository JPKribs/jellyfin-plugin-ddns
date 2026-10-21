using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Domeneshop (port of ddclient's <c>nic_domeneshop_update</c>). Basic-auth GET to
/// <c>/v0/dyndns/update?hostname=&lt;host&gt;&amp;myip=&lt;ip&gt;</c>, one call per enabled IP
/// version. Success is any 2xx HTTP status. Login is the API token, password the API secret.
/// </summary>
public sealed class DomeneshopProvider : DNSProviderBase
{
    private const string DefaultServer = "api.domeneshop.no";

    /// <summary>Initializes a new instance of the <see cref="DomeneshopProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DomeneshopProvider(IHttpClientFactory httpClientFactory, ILogger<DomeneshopProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Domeneshop;

    /// <inheritdoc />
    public override string Label => "Domeneshop";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your Domeneshop API token and secret. Hostname is the host.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Host",
        Login = "API token",
        Password = "API secret",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A login (API token) and password (API secret) are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => UpdateOneAsync(server, record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> UpdateOneAsync(
        string server,
        DNSRecord record,
        string ipValue,
        CancellationToken cancellationToken)
    {
        var url = server
            + "/v0/dyndns/update?hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(ipValue);

        var result = await SendAsync(
            HttpMethod.Get,
            url,
            cancellationToken,
            login: record.Login,
            password: record.Password).ConfigureAwait(false);

        return result.Ok
            ? (true, "set to " + ipValue)
            : (false, "failed (HTTP " + result.Status + ")");
    }
}
