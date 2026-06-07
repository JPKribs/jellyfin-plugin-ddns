using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Shared logic for the provider implementations: per-family aggregation, server resolution, and HTTP
/// over the shared <see cref="JpkHttp"/> helper.
/// </summary>
public abstract class DnsProviderBase : IDnsProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DnsProviderBase"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    protected DnsProviderBase(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract DnsProviderKind Kind { get; }

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public abstract Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes one address. Returns whether it succeeded plus a short message; the base aggregates these.
    /// </summary>
    /// <param name="recordType">The DNS record type, <c>"A"</c> or <c>"AAAA"</c>.</param>
    /// <param name="address">The IP address to set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Success flag and a short per-address message.</returns>
    protected delegate Task<(bool Ok, string Message)> FamilyUpdater(string recordType, string address, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="update"/> once per enabled+detected family (A then AAAA) and aggregates the
    /// outcomes into a single result. The provider only writes per-address logic.
    /// </summary>
    /// <param name="record">The record being updated.</param>
    /// <param name="ip">The detected addresses.</param>
    /// <param name="update">The per-address updater.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aggregated result; failure when no family is enabled/detected.</returns>
    protected static async Task<DnsUpdateResult> ApplyPerFamilyAsync(
        DnsRecord record,
        DetectedIp ip,
        FamilyUpdater update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(update);

        var parts = new List<string>();
        var allOk = true;

        if (record.UpdateIPv4 && ip.IPv4 is not null)
        {
            var (ok, message) = await update("A", ip.IPv4, cancellationToken).ConfigureAwait(false);
            allOk &= ok;
            parts.Add("IPv4: " + message);
        }

        if (record.UpdateIPv6 && ip.IPv6 is not null)
        {
            var (ok, message) = await update("AAAA", ip.IPv6, cancellationToken).ConfigureAwait(false);
            allOk &= ok;
            parts.Add("IPv6: " + message);
        }

        return parts.Count == 0
            ? DnsUpdateResult.Fail("No record type enabled or no matching IP detected.")
            : new DnsUpdateResult(allOk, string.Join("; ", parts));
    }

    /// <summary>
    /// Returns the configured server override, or the provider default, with an <c>https://</c> scheme.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="defaultServer">The provider default server/base.</param>
    /// <returns>A URL base beginning with a scheme.</returns>
    protected static string ServerBase(DnsRecord record, string defaultServer)
    {
        var server = string.IsNullOrWhiteSpace(record.Server) ? defaultServer : record.Server.Trim();
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            server = "https://" + server;
        }

        return server.TrimEnd('/');
    }

    /// <summary>
    /// Sends an HTTP request through the shared helper and logs network failures.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="contentType">The body content type.</param>
    /// <param name="login">Optional basic-auth user.</param>
    /// <param name="password">Optional basic-auth password.</param>
    /// <returns>The HTTP result, with status zero on a network failure.</returns>
    protected async Task<HttpResult> SendAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        string? body = null,
        string contentType = "application/json",
        string? login = null,
        string? password = null)
    {
        var result = await JpkHttp.SendAsync(_httpClientFactory, method, url, cancellationToken, headers, body, contentType, login, password).ConfigureAwait(false);
        if (result.Status == 0)
        {
            Logger.LogWarning("{Provider} request to {Url} failed: {Detail}", Kind, Redact(url), result.Body);
        }

        return result;
    }

    /// <summary>Removes a query string so credentials are never logged.</summary>
    /// <param name="url">The URL to redact.</param>
    /// <returns>The URL without its query string.</returns>
    protected static string Redact(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : string.Concat(url.AsSpan(0, q), "?<redacted>");
    }

    /// <summary>Returns the first whitespace-delimited token of a response body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The first token, or an empty string.</returns>
    protected static string FirstToken(string body) => JpkHttp.FirstToken(body);

    /// <summary>Returns the first line of a response body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The first line, trimmed.</returns>
    protected static string FirstLine(string body) => JpkHttp.FirstLine(body);
}
