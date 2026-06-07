using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// easyDNS (port of ddclient's <c>nic_easydns_update</c>). Basic-auth GET to <c>/dyn/generic.php</c> with
/// the hostname and IP (optionally wildcard and MX). <see cref="DnsRecord.Login"/>/<see cref="DnsRecord.Password"/>
/// are the easyDNS credentials, <see cref="DnsRecord.Hostname"/> is the fully qualified host, and
/// <see cref="DnsRecord.Server"/> overrides the API host. The HTML reply is stripped of tags and scanned
/// for a status token; <c>NOERROR</c> or <c>OK</c> mean success.
/// </summary>
public sealed class EasyDnsProvider : DnsProviderBase
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
    public override DnsProviderKind Kind => DnsProviderKind.EasyDns;

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

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => SendUpdateAsync(record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> SendUpdateAsync(DnsRecord record, string ip, CancellationToken cancellationToken)
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
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "NOERROR", StringComparison.Ordinal)
                || string.Equals(token, "OK", StringComparison.Ordinal)
                || Errors.ContainsKey(token))
            {
                return token;
            }
        }

        // "ILLEGAL INPUT" is a two-word status, so token splitting misses it.
        if (body.Contains("ILLEGAL INPUT", StringComparison.Ordinal))
        {
            return "ILLEGAL INPUT";
        }

        return string.Empty;
    }
}
