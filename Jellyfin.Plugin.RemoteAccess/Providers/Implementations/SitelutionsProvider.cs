using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Sitelutions (port of ddclient's <c>nic_sitelutions_update</c>). A GET to <c>/dnsup</c> per record
/// type with the record id, email login, password and IP as query parameters. <see cref="DNSRecord.Hostname"/>
/// is the numeric record id, <see cref="DNSRecord.Login"/> the account email and
/// <see cref="DNSRecord.Password"/> the password.
/// </summary>
public sealed class SitelutionsProvider : DNSProviderBase
{
    private const string DefaultServer = "api2.sitelutions.com";

    /// <summary>Initializes a new instance of the <see cref="SitelutionsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SitelutionsProvider(IHttpClientFactory httpClientFactory, ILogger<SitelutionsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Sitelutions;

    /// <inheritdoc />
    public override string Label => "Sitelutions";

    /// <inheritdoc />
    public override string Hint => "Hostname is the numeric record ID. Login is the account email. Password is the account password.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Record ID",
        Login = "Account email",
        Password = "Account password",
        Server = true,
        Ttl = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("A record id (hostname) is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("A login (email) and password are required.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record.Hostname, record.Login, record.Password, record.Ttl, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string hostname,
        string login,
        string password,
        int ttl,
        string address,
        CancellationToken cancellationToken)
    {
        var url = server
            + "/dnsup?id=" + Uri.EscapeDataString(hostname)
            + "&user=" + Uri.EscapeDataString(login)
            + "&pass=" + Uri.EscapeDataString(password)
            + "&ip=" + Uri.EscapeDataString(address);

        // ddclient sends no TTL by default, so only pass one the user actually set (above the 1 sentinel).
        if (ttl > 1)
        {
            url += "&ttl=" + ttl.ToString(CultureInfo.InvariantCulture);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        // Sitelutions signals success only by the literal word "success" appearing in the reply.
        return result.Body.Contains("success", StringComparison.OrdinalIgnoreCase)
            ? (true, "updated to " + address)
            : (false, "invalid reply: " + FirstLine(result.Body));
    }
}
