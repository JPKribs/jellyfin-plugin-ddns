using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// DNS Made Easy (port of ddclient's <c>nic_dnsmadeeasy_update</c>). The login is the account email
/// address, the password is the generated dynamic-DNS record password, and the hostname field holds
/// the numeric dynamic-DNS record ID. Updates use the <c>/servlet/updateip</c> query-string endpoint.
/// </summary>
public sealed class DnsMadeEasyProvider : DnsProviderBase
{
    private const string DefaultServer = "cp.dnsmadeeasy.com";
    private const string Script = "/servlet/updateip";

    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["error-auth"] = "Invalid username or password, or invalid IP syntax",
        ["error-auth-suspend"] = "User has had their account suspended due to complaints or misuse of the service.",
        ["error-auth-voided"] = "User has had their account permanently revoked.",
        ["error-record-invalid"] = "Record ID number does not exist in the system.",
        ["error-record-auth"] = "User does not have access to this record.",
        ["error-record-ip-same"] = "No update required.",
        ["error-system"] = "General system error which is caught and recognized by the system.",
        ["error"] = "General system error unrecognized by the system.",
        ["success"] = "Record successfully updated!",
    };

    /// <summary>Initializes a new instance of the <see cref="DnsMadeEasyProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DnsMadeEasyProvider(IHttpClientFactory httpClientFactory, ILogger<DnsMadeEasyProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DnsProviderKind Kind => DnsProviderKind.DnsMadeEasy;

    /// <inheritdoc />
    public override async Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname)
            || string.IsNullOrWhiteSpace(record.Login)
            || string.IsNullOrWhiteSpace(record.Password))
        {
            return DnsUpdateResult.Fail("DNS Made Easy requires a record ID (hostname), login (email), and password.");
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, record, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        DnsRecord record,
        string value,
        CancellationToken cancellationToken)
    {
        var url = server + Script
            + "?username=" + Uri.EscapeDataString(record.Login)
            + "&password=" + Uri.EscapeDataString(record.Password)
            + "&ip=" + Uri.EscapeDataString(value)
            + "&id=" + Uri.EscapeDataString(record.Hostname);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status.ToString(CultureInfo.InvariantCulture));
        }

        // ddclient inspects the last (non-empty) line of the reply and requires it to contain "success".
        var returned = LastNonEmptyLine(result.Body);
        if (returned.Contains("success", StringComparison.Ordinal))
        {
            return (true, "set to " + value);
        }

        var detail = Messages.TryGetValue(returned, out var known)
            ? returned + ": " + known
            : returned;
        return (false, "failed (server said: " + detail + ")");
    }

    private static string LastNonEmptyLine(string body)
    {
        var lines = (body ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }
}
