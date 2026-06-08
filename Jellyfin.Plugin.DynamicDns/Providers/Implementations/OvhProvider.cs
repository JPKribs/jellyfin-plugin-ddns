using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// OVH (port of ddclient's <c>nic_ovh_update</c>). Sends a DynDNS-style GET to
/// <c>/nic/update?system=dyndns&amp;hostname=&amp;myip=</c> using HTTP basic auth with
/// <see cref="DNSRecord.Login"/>/<see cref="DNSRecord.Password"/>. <see cref="DNSRecord.Hostname"/> is the
/// record to update and <see cref="DNSRecord.Server"/> overrides the endpoint host. A reply containing
/// <c>good</c> or <c>nochg</c> is success.
/// </summary>
public sealed class OvhProvider : DNSProviderBase
{
    private const string DefaultServer = "www.ovh.com";
    private const string Script = "/nic/update";

    /// <summary>Initializes a new instance of the <see cref="OvhProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public OvhProvider(IHttpClientFactory httpClientFactory, ILogger<OvhProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Ovh;

    /// <inheritdoc />
    public override string Label => "OVH";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your OVH DynHost credentials.";

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

        var ipv4 = record.UpdateIPv4 ? ip.IPv4 : null;
        var ipv6 = record.UpdateIPv6 ? ip.IPv6 : null;

        // OVH's DynDNS endpoint accepts a single 'myip' value per update (ddclient's $wantip).
        var wantip = ipv4 ?? ipv6;
        if (wantip is null)
        {
            return DNSUpdateResult.Fail("No record type enabled or no matching IP detected.");
        }

        var url = ServerBase(record, DefaultServer) + Script
            + "?system=dyndns&hostname=" + Uri.EscapeDataString(record.Hostname)
            + "&myip=" + Uri.EscapeDataString(wantip);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (result.Status == 0)
        {
            return DNSUpdateResult.Fail("Could not connect to the OVH server.");
        }

        if (!result.Ok)
        {
            return DNSUpdateResult.Fail("HTTP " + result.Status + ".");
        }

        // ddclient's dyndns2 reads the first token of the reply, so match that rather than the whole body.
        var body = result.Body ?? string.Empty;
        var status = FirstToken(body);

        if (string.Equals(status, "good", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Ok("IP address set to " + wantip + ".");
        }

        if (string.Equals(status, "nochg", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Ok("Skipped: IP address was already set to " + wantip + ".");
        }

        return DNSUpdateResult.Fail("server said: " + FirstLine(body));
    }
}
