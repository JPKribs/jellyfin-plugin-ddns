using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Key-Systems (port of ddclient's <c>nic_keysystems_update</c>). Sends one
/// <c>server/update.php?hostname=&amp;password=&amp;ip=</c> GET per address and treats a body
/// containing <c>code = 200</c> as success. <see cref="DnsRecord.Hostname"/> is the host and
/// <see cref="DnsRecord.Password"/> is the service password; <see cref="DnsRecord.Server"/> overrides
/// the default endpoint.
/// </summary>
public sealed class KeySystemsProvider : DnsProviderBase
{
    private const string DefaultServer = "dynamicdns.key-systems.net";

    /// <summary>Initializes a new instance of the <see cref="KeySystemsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public KeySystemsProvider(IHttpClientFactory httpClientFactory, ILogger<KeySystemsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.KeySystems;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("Key-Systems requires a hostname and a password.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record.Hostname, record.Password, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string hostname,
        string password,
        string value,
        CancellationToken cancellationToken)
    {
        var url = server + "/update.php?hostname=" + Uri.EscapeDataString(hostname)
            + "&password=" + Uri.EscapeDataString(password)
            + "&ip=" + Uri.EscapeDataString(value);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        return result.Body.Contains("code = 200", StringComparison.Ordinal)
            ? (true, "set to " + value)
            : (false, "failed (" + FirstLine(result.Body) + ")");
    }
}
