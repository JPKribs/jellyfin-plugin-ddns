using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// easyDNS (port of ddclient's <c>nic_easydns_update</c>). Basic-auth GET to <c>/dyn/generic.php</c> with
/// the hostname and IP (optionally wildcard and MX). <see cref="DNSRecord.Login"/>/<see cref="DNSRecord.Password"/>
/// are the easyDNS credentials, <see cref="DNSRecord.Hostname"/> is the fully qualified host, and
/// <see cref="DNSRecord.Server"/> overrides the API host. The HTML reply is stripped of tags and scanned
/// for a status token. <c>NOERROR</c> or <c>OK</c> mean success.
/// </summary>
public sealed class EasyDnsProvider : DNSProviderBase
{
    private const string DefaultServer = "api.cp.easydns.com";
    private const string Script = "/dyn/generic.php";

    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["NOACCESS"] = "Authentication failed. This happens if the username/password OR host or domain are wrong.",
        ["NO_AUTH"] = "Authentication failed. This happens if the username/password OR host or domain are wrong.",
        ["NOSERVICE"] = "Dynamic DNS is not turned on for this domain.",
        ["ILLEGAL INPUT"] = "Client sent data that is not allowed in a dynamic DNS update.",
        ["TOOSOON"] = "Update frequency is too high.",
        ["TOO_FREQ"] = "Update frequency is too high."
    };

    /// <summary>Initializes a new instance of the <see cref="EasyDnsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public EasyDnsProvider(IHttpClientFactory httpClientFactory, ILogger<EasyDnsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.EasyDns;

    /// <inheritdoc />
    public override string Label => "easyDNS";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your easyDNS API credentials.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Hostname",
        Login = "Username",
        Password = "API token",
        Server = true,
        Advanced = new[] { "wildcard", "mx", "backupmx" },
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
            (type, address, ct) => SendUpdateAsync(record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(DNSRecord record, string ip, CancellationToken cancellationToken)
    {
        var url = new StringBuilder(ServerBase(record, DefaultServer))
            .Append(Script)
            .Append("?hostname=")
            .Append(Uri.EscapeDataString(record.Hostname))
            .Append("&myip=")
            .Append(Uri.EscapeDataString(ip));

        url.Append("&wildcard=").Append(record.Wildcard ? "ON" : "OFF");

        if (!string.IsNullOrWhiteSpace(record.Mx))
        {
            url.Append("&mx=")
                .Append(Uri.EscapeDataString(record.Mx))
                .Append("&backmx=")
                .Append(record.BackupMx ? "YES" : "NO");
        }

        var result = await SendAsync(
            HttpMethod.Get,
            url.ToString(),
            cancellationToken,
            login: record.Login,
            password: record.Password).ConfigureAwait(false);

        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status + ".");
        }

        var body = WhitespaceRegex.Replace(TagRegex.Replace(result.Body ?? string.Empty, " "), " ").Trim();
        var status = ExtractStatus(body);

        if (string.Equals(status, "NOERROR", StringComparison.Ordinal)
            || string.Equals(status, "OK", StringComparison.Ordinal))
        {
            return (true, "good (" + ip + ")");
        }

        if (Errors.TryGetValue(status, out var description))
        {
            return (false, status + ": " + description);
        }

        return (false, "unexpected result: " + body);
    }

    private static string ExtractStatus(string body)
    {
        var status = body.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token =>
                string.Equals(token, "NOERROR", StringComparison.Ordinal)
                || string.Equals(token, "OK", StringComparison.Ordinal)
                || Errors.ContainsKey(token));
        if (status is not null)
        {
            return status;
        }

        // "ILLEGAL INPUT" is a two-word status, so token splitting misses it.
        if (body.Contains("ILLEGAL INPUT", StringComparison.Ordinal))
        {
            return "ILLEGAL INPUT";
        }

        return string.Empty;
    }
}
