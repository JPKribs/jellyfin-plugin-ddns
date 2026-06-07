using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Dinahosting (port of ddclient's <c>nic_dinahosting_update</c>). Basic-auth GET against the dinahosting
/// API using <see cref="DnsRecord.Login"/>/<see cref="DnsRecord.Password"/>; <see cref="DnsRecord.Hostname"/>
/// is split into the first label (host) and the remainder (domain), and the <c>Domain_Zone_UpdateType</c>
/// command sets the A or AAAA record. <see cref="DnsRecord.Server"/> overrides the API host.
/// </summary>
public sealed class DinahostingProvider : DnsProviderBase
{
    private const string DefaultServer = "dinahosting.com";
    private const string Script = "/special/api.php";

    /// <summary>Initializes a new instance of the <see cref="DinahostingProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DinahostingProvider(IHttpClientFactory httpClientFactory, ILogger<DinahostingProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.Dinahosting;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Login) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("A login and password are required.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DnsUpdateResult.Fail("A hostname is required.");
        }

        var dot = record.Hostname.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot >= record.Hostname.Length - 1)
        {
            return DnsUpdateResult.Fail("Hostname must be fully qualified (host.domain.tld).");
        }

        var hostname = record.Hostname.Substring(0, dot);
        var domain = record.Hostname.Substring(dot + 1);
        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(record, server, hostname, domain, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        DnsRecord record,
        string server,
        string hostname,
        string domain,
        string type,
        string address,
        CancellationToken cancellationToken)
    {
        var url = server + Script
            + "?hostname=" + Uri.EscapeDataString(hostname)
            + "&domain=" + Uri.EscapeDataString(domain)
            + "&command=Domain_Zone_UpdateType" + type
            + "&ip=" + Uri.EscapeDataString(address);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (result.Status == 0)
        {
            return (false, result.Body);
        }

        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (result.Body.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, address + " set");
        }

        var code = ExtractField(result.Body, "responseCode = ", false) ?? "<undefined>";
        var message = ExtractField(result.Body, "errors_0_message = '", true) ?? "<undefined>";
        return (false, "error " + code + ": " + message);
    }

    private static string? ExtractField(string body, string prefix, bool quoted)
    {
        var idx = body.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + prefix.Length;
        var end = quoted ? body.IndexOf('\'', start) : FindLineEnd(body, start);
        if (end < 0)
        {
            end = body.Length;
        }

        return body.Substring(start, end - start);
    }

    private static int FindLineEnd(string body, int start)
    {
        for (var i = start; i < body.Length; i++)
        {
            if (body[i] == '\r' || body[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
