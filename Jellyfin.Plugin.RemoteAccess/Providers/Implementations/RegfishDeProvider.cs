using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// regfish.de (port of ddclient's <c>nic_regfishde_update</c>). A single GET request to
/// <c>/?fqdn=&amp;forcehost=1&amp;token=</c> carries the new IPv4 and/or IPv6 address. The server
/// returns a body containing <c>success</c> on completion. <see cref="DNSRecord.Password"/> holds
/// the regfish update token.
/// </summary>
public sealed class RegfishDeProvider : DNSProviderBase
{
    private const string DefaultServer = "dyndns.regfish.de";

    // "Success" as a word of its own, so a reply such as "unsuccessful" is never read as success.
    private static readonly Regex SuccessWord = new(
        "\\bsuccess",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Initializes a new instance of the <see cref="RegfishDeProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public RegfishDeProvider(IHttpClientFactory httpClientFactory, ILogger<RegfishDeProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.RegfishDe;

    /// <inheritdoc />
    public override string Label => "regfish.de";

    /// <inheritdoc />
    public override string Hint => "Update token is the regfish DDNS token for the record.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Password = "Update token",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname) || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("regfish.de requires a hostname and an update token (password).");
        }

        var wantIPv4 = record.UpdateIPv4 && ip.IPv4 is not null;
        var wantIPv6 = record.UpdateIPv6 && ip.IPv6 is not null;
        if (!wantIPv4 && !wantIPv6)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var server = ServerBase(record, DefaultServer);
        var url = server + "/?fqdn=" + Uri.EscapeDataString(record.Hostname)
            + "&forcehost=1&token=" + Uri.EscapeDataString(record.Password);
        if (wantIPv4)
        {
            url += "&ipv4=" + Uri.EscapeDataString(ip.IPv4!);
        }

        if (wantIPv6)
        {
            url += "&ipv6=" + Uri.EscapeDataString(ip.IPv6!);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("regfish.de returned HTTP " + result.Status + ".");
        }

        if (!SuccessWord.IsMatch(result.Body))
        {
            return DNSUpdateResult.Fail("server said: " + FirstLine(result.Body));
        }

        var detail = wantIPv4 && wantIPv6
            ? "IPv4 set to " + ip.IPv4 + " and IPv6 set to " + ip.IPv6
            : wantIPv4 ? "IPv4 set to " + ip.IPv4 : "IPv6 set to " + ip.IPv6;
        return WithFamilies(DNSUpdateResult.Ok("regfish.de: " + detail + "."), wantIPv4, wantIPv6);
    }
}
