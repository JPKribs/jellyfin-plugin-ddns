using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// Directnic (port of ddclient's <c>nic_directnic_update</c>). Directnic has no username/password.
/// It issues per-record "gateway" update URLs. Map the IPv4 gateway URL onto <see cref="DNSRecord.Login"/>
/// and the IPv6 gateway URL onto <see cref="DNSRecord.Password"/> (ddclient's <c>urlv4</c>/<c>urlv6</c>).
/// The IP is appended as a <c>?data=&lt;ip&gt;</c> query parameter and success is detected by a JSON body
/// whose <c>result</c> field equals <c>success</c>.
/// </summary>
public sealed class DirectnicProvider : DNSProviderBase
{
    private const string DefaultServer = "directnic.com";

    /// <summary>Initializes a new instance of the <see cref="DirectnicProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public DirectnicProvider(IHttpClientFactory httpClientFactory, ILogger<DirectnicProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Directnic;

    /// <inheritdoc />
    public override string Label => "Directnic";

    /// <inheritdoc />
    public override string Hint => "Login is your IPv4 gateway URL and Password is your IPv6 gateway URL. Paste the full per record gateway URLs.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Login = "IPv4 gateway URL",
        Password = "IPv6 gateway URL",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(record, type, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        DNSRecord record,
        string type,
        string value,
        CancellationToken cancellationToken)
    {
        // ddclient picks the gateway URL per family: urlv4 for A, urlv6 for AAAA.
        var isV4 = string.Equals(type, "A", StringComparison.Ordinal);
        var gateway = isV4 ? record.Login : record.Password;
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return (false, isV4
                ? "Directnic requires the IPv4 gateway URL in the Login field."
                : "Directnic requires the IPv6 gateway URL in the Password field.");
        }

        var baseUrl = ResolveGatewayUrl(record, gateway);
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = string.Concat(baseUrl, separator, "data=", Uri.EscapeDataString(value));

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var resultElement)
                && resultElement.ValueKind == JsonValueKind.String
                && string.Equals(resultElement.GetString(), "success", StringComparison.Ordinal))
            {
                return (true, "set to " + value);
            }

            return (false, "server said: " + FirstLine(result.Body));
        }
        catch (JsonException)
        {
            return (false, "response is not a JSON object: " + FirstLine(result.Body));
        }
    }

    private string ResolveGatewayUrl(DNSRecord record, string configured)
    {
        var trimmed = configured.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // The gateway URL embeds the update token, so plain http exposes it in transit.
            WarnIfPlainHttp(trimmed);
            return trimmed;
        }

        // Allow a bare gateway path/token, anchored to the (overridable) Directnic host.
        return string.Concat(ServerBase(record, DefaultServer), "/", trimmed.TrimStart('/'));
    }
}
