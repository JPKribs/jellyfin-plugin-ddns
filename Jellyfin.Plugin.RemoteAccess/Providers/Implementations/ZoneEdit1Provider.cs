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
/// ZoneEdit (port of ddclient's <c>nic_zoneedit1_update</c>): a basic-auth GET to
/// <c>/auth/dynamic.html</c> per record type. Login/Password are the ZoneEdit credentials, Hostname is
/// the host to update, Zone optionally scopes the update, and Server optionally overrides the endpoint.
/// The reply body carries an XML-ish <c>&lt;SUCCESS&gt;</c>/<c>&lt;ERROR&gt;</c> tag whose
/// <c>CODE</c>/<c>TEXT</c> attributes report the outcome. An <c>ERROR</c> with <c>CODE="707"</c> is
/// treated as success, matching ddclient.
/// </summary>
public sealed class ZoneEdit1Provider : DNSProviderBase
{
    private const string DefaultServer = "dynamic.zoneedit.com";

    private static readonly Regex StatusRegex = new(
        "<(SUCCESS|ERROR)\\s+([^>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex AssignmentRegex = new(
        "(\\w+)\\s*=\\s*\"([^\"]*)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Initializes a new instance of the <see cref="ZoneEdit1Provider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ZoneEdit1Provider(IHttpClientFactory httpClientFactory, ILogger<ZoneEdit1Provider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.ZoneEdit1;

    /// <inheritdoc />
    public override string Label => "ZoneEdit";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your ZoneEdit username and dynamic DNS token. Zone is optional.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "DDNS token",
        Zone = "Zone",
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
            (type, address, ct) => PushAsync(record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(DNSRecord record, string address, CancellationToken cancellationToken)
    {
        var url = ServerBase(record, DefaultServer)
            + "/auth/dynamic.html?host=" + Uri.EscapeDataString(record.Hostname)
            + "&dnsto=" + Uri.EscapeDataString(address);

        if (!string.IsNullOrWhiteSpace(record.Zone))
        {
            url += "&zone=" + Uri.EscapeDataString(record.Zone);
        }

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, login: record.Login, password: record.Password).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        return Parse(result.Body, address);
    }

    private static (bool Ok, string Message) Parse(string body, string ipValue)
    {
        var match = StatusRegex.Match(body ?? string.Empty);
        if (!match.Success)
        {
            return (false, "no recognizable response: " + FirstLine(body ?? string.Empty));
        }

        var status = match.Groups[1].Value;
        var assignments = match.Groups[2].Value;

        string code = "999";
        string text = string.Empty;
        var statusIp = ipValue;

        foreach (Match a in AssignmentRegex.Matches(assignments))
        {
            var key = a.Groups[1].Value;
            var val = a.Groups[2].Value;
            if (string.Equals(key, "CODE", StringComparison.OrdinalIgnoreCase))
            {
                code = val;
            }
            else if (string.Equals(key, "TEXT", StringComparison.OrdinalIgnoreCase))
            {
                text = val;
            }
            else if (string.Equals(key, "IP", StringComparison.OrdinalIgnoreCase))
            {
                statusIp = val;
            }
        }

        var isSuccess = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
                && string.Equals(code, "707", StringComparison.Ordinal));

        if (isSuccess)
        {
            return (true, $"IP address set to {statusIp} ({code}: {text})");
        }

        return (false, $"{code}: {text}");
    }
}
