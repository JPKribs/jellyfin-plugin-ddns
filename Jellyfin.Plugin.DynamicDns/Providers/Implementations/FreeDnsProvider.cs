using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// FreeDNS / afraid.org (port of ddclient's <c>nic_freedns_update</c>, API v1). <see cref="DNSRecord.Login"/>
/// and <see cref="DNSRecord.Password"/> are the service credentials. Their SHA-1 hash (<c>login|password</c>)
/// authenticates the record-list query. <see cref="DNSRecord.Hostname"/> selects the record(s) to update, and
/// <see cref="DNSRecord.Server"/> overrides the default host. An update fetches the existing record list, then
/// visits each record's per-record update URL with the new address.
/// </summary>
public sealed class FreeDnsProvider : DNSProviderBase
{
    private const string DefaultServer = "freedns.afraid.org";

    private static readonly char[] LineSeparators = { '\n' };
    private static readonly char[] FieldSeparator = { '|' };

    /// <summary>Initializes a new instance of the <see cref="FreeDnsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public FreeDnsProvider(IHttpClientFactory httpClientFactory, ILogger<FreeDnsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.FreeDns;

    /// <inheritdoc />
    public override string Label => "FreeDNS (afraid.org)";

    /// <inheritdoc />
    public override string Hint => "Login and Password are your afraid.org credentials, used to derive the update token.";

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
            return DNSUpdateResult.Fail("FreeDNS requires a login and password.");
        }

        if (string.IsNullOrWhiteSpace(record.Hostname))
        {
            return DNSUpdateResult.Fail("FreeDNS requires a hostname.");
        }

        var server = ServerBase(record, DefaultServer);
        var sha = Sha1Hex(record.Login + "|" + record.Password);
        var listUrl = server + "/api/?action=getdyndns&v=2&sha=" + sha;

        var listReply = await SendAsync(HttpMethod.Get, listUrl, cancellationToken).ConfigureAwait(false);
        if (!listReply.Ok)
        {
            return DNSUpdateResult.Fail("failed to get record list (HTTP " + listReply.Status + ").");
        }

        // Bucket the matching host's records by the address type they currently hold so we never switch a
        // record from one family to the other. A NULL/empty current address counts as IPv4.
        string? recIPv4 = null;
        string? currentIPv4 = null;
        string? recIPv6 = null;
        string? currentIPv6 = null;
        var anyRecord = false;

        var lines = listReply.Body.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var rec = line.Split(FieldSeparator);
            if (rec.Length < 3)
            {
                continue;
            }

            anyRecord = true;
            if (!string.Equals(rec[0].Trim(), record.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = rec[1].Trim();
            var updateUrl = rec[2].Trim();
            if (IsIPv6(current))
            {
                recIPv6 ??= updateUrl;
                currentIPv6 ??= current;
            }
            else
            {
                recIPv4 ??= updateUrl;
                currentIPv4 ??= current;
            }
        }

        if (!anyRecord)
        {
            return DNSUpdateResult.Fail("failed to get record list from " + Redact(listUrl) + ".");
        }

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => string.Equals(type, "AAAA", StringComparison.Ordinal)
                ? ApplyAsync("AAAA", recIPv6, currentIPv6, address, ct)
                : ApplyAsync("A", recIPv4, currentIPv4, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is mandated by the FreeDNS API v1 credential scheme; it is not used to protect data.")]
    private static string Sha1Hex(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static bool IsIPv6(string value)
        => value.Contains(':', StringComparison.Ordinal);

    private async Task<(bool Ok, string Message)> ApplyAsync(
        string type,
        string? updateUrl,
        string? currentAddress,
        string newAddress,
        CancellationToken cancellationToken)
    {
        if (updateUrl is null)
        {
            return (false, "no '" + type + "' record at FreeDNS");
        }

        if (string.Equals(currentAddress, newAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "'" + type + "' record already set to " + newAddress);
        }

        var separator = updateUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = updateUrl + separator + "address=" + Uri.EscapeDataString(newAddress);

        var reply = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return (false, "HTTP " + reply.Status);
        }

        // ddclient success regex /Updated.*<host>.*to.*<ip>/: presence of "Updated" + the new address.
        if (reply.Body.Contains("Updated", StringComparison.Ordinal)
            && reply.Body.Contains(newAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "set to " + newAddress);
        }

        return (false, "failed (" + FirstLine(reply.Body) + ")");
    }
}
