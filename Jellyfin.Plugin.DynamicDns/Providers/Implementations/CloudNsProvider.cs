using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// ClouDNS (port of ddclient's <c>nic_cloudns_update</c>). The per-record "DynURL" already embeds the
/// record key in its query string and is stored in <see cref="DNSRecord.Password"/>. Ddclient fetches
/// that URL with <c>&amp;proxy=1</c> appended and supplies the desired address via <c>X-Forwarded-For</c>.
/// The same DynURL is reused for whichever of A/AAAA are enabled.
/// </summary>
public sealed class CloudNsProvider : DNSProviderBase
{
    private const string WrongKeyReply = "The record's key is wrong!";
    private const string InvalidReply = "Invalid request.";

    /// <summary>Initializes a new instance of the <see cref="CloudNsProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public CloudNsProvider(IHttpClientFactory httpClientFactory, ILogger<CloudNsProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.CloudNs;

    /// <inheritdoc />
    public override string Label => "ClouDNS";

    /// <inheritdoc />
    public override string Hint => "Password is the full ClouDNS DynURL, which is the secret update URL. Hostname, Login and Zone are not used.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Password = "Update URL",
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        var dynUrl = record.Password;
        if (string.IsNullOrWhiteSpace(dynUrl))
        {
            return DNSUpdateResult.Fail("ClouDNS requires the record's DynURL (stored in the Password field).");
        }

        dynUrl = dynUrl.Trim();
        if (!dynUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !dynUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return DNSUpdateResult.Fail("ClouDNS DynURL must be a full http(s) URL.");
        }

        // The DynURL embeds the update secret, so plain http exposes it in transit.
        WarnIfPlainHttp(dynUrl);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(dynUrl, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(string dynUrl, string value, CancellationToken cancellationToken)
    {
        // ddclient appends &proxy=1 so ClouDNS reads the desired IP from X-Forwarded-For.
        var separator = dynUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = string.Concat(dynUrl, separator, "proxy=1");

        var headers = new[] { new KeyValuePair<string, string>("X-Forwarded-For", value) };

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken, headers: headers).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        var reply = FirstLine(result.Body).Trim();
        if (string.Equals(reply, WrongKeyReply, StringComparison.Ordinal)
            || string.Equals(reply, InvalidReply, StringComparison.Ordinal))
        {
            return (false, "failed (" + reply + ")");
        }

        // ClouDNS does not document success replies, so any non-error reply is treated as success.
        return (true, "set to " + value);
    }
}
