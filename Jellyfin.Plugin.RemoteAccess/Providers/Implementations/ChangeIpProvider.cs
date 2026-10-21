using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// ChangeIP (port of ddclient's <c>nic_changeip_update</c>). Login/Password are the basic-auth
/// credentials, Hostname is the record to update, and Server optionally overrides the default endpoint.
/// </summary>
public sealed class ChangeIpProvider : DNSProviderBase
{
    private const string DefaultServer = "nic.changeip.com";

    /// <summary>Initializes a new instance of the <see cref="ChangeIpProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ChangeIpProvider(IHttpClientFactory httpClientFactory, ILogger<ChangeIpProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.ChangeIp;

    /// <inheritdoc />
    public override string Label => "ChangeIP";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your ChangeIP account credentials.";

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

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SendUpdateAsync(record, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(DNSRecord record, string type, string address, CancellationToken cancellationToken)
    {
        var url = ServerBase(record, DefaultServer)
            + "/nic/update?hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        // ChangeIP replies with a status line such as "200 Successful Update". Match the first line only
        // so an error page that merely mentions the word elsewhere can't be read as success.
        var firstLine = FirstLine(result.Body ?? string.Empty);
        if (firstLine.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, type + " set to " + address);
        }

        return (false, "invalid reply: " + firstLine);
    }
}
