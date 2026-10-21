using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// DonDominio (port of ddclient's <c>nic_dondominio_update</c>). Issues a GET per record type to
/// <c>/plain/?user=&amp;password=&amp;host=&amp;ip=</c>. <see cref="DNSRecord.Login"/> is the user and
/// <see cref="DNSRecord.Password"/> the API key. <see cref="DNSRecord.Hostname"/> is the host. The update
/// succeeds when the reply's last line contains <c>OK</c> or <c>IP:&lt;ip&gt;</c>.
/// </summary>
public sealed class DonDominioProvider : DNSProviderBase
{
    private const string DefaultServer = "dondns.dondominio.com";

    /// <summary>Initializes a new instance of the <see cref="DonDominioProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DonDominioProvider(IHttpClientFactory httpClientFactory, ILogger<DonDominioProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.DonDominio;

    /// <inheritdoc />
    public override string Label => "DonDominio";

    /// <inheritdoc />
    public override string Hint => "Login is the user. Password is your DonDominio API key. Hostname is the host.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Host",
        Login = "User",
        Password = "API key",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A login (user) and password (API key) are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A hostname is required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SendUpdateAsync(server, record.Login, record.Password, record.Hostname, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(
        string server,
        string login,
        string password,
        string hostname,
        string address,
        CancellationToken cancellationToken)
    {
        var url = server
            + "/plain/?user=" + Uri.EscapeDataString(login)
            + "&password=" + Uri.EscapeDataString(password)
            + "&host=" + Uri.EscapeDataString(hostname)
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        // Match the OK token rather than the substring, so a word like "LOOKUP" is not read as success.
        var returned = LastLine(result.Body);
        if (string.Equals(FirstToken(returned), "OK", StringComparison.Ordinal)
            || returned.Contains("IP:" + address, StringComparison.Ordinal))
        {
            return (true, "set to " + address);
        }

        return (false, "server said: " + returned);
    }

    private static string LastLine(string body)
    {
        var trimmed = (body ?? string.Empty).TrimEnd('\r', '\n', ' ', '\t');
        var nl = trimmed.LastIndexOf('\n');
        return nl < 0 ? trimmed.Trim() : trimmed[(nl + 1)..].Trim();
    }
}
