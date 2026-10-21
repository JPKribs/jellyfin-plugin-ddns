using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Discovers the server's current public IPv4/IPv6 addresses from configurable https echo endpoints.
/// To resist a single bad or hijacked endpoint, two endpoints must agree on the address before it is
/// accepted. The plugin keeps querying the list until two agree, and if only one endpoint ever responds
/// it trusts that one. Plain http endpoints are refused since their reply could be tampered with.
/// </summary>
public sealed class IPDetectionService
{
    private static readonly char[] EndpointSeparators = { '\n', '\r', '\t', ',', ' ' };

    // An echo endpoint answers with one address. Anything longer is not an address, so at most this many
    // characters are read, which stops a misbehaving endpoint from streaming an unbounded body.
    private const int MaxResponseChars = 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IPDetectionService> _logger;
    private readonly ConcurrentDictionary<string, RepeatedProblem> _problems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warnedEndpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="IPDetectionService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public IPDetectionService(IHttpClientFactory httpClientFactory, ILogger<IPDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Queries the configured (and fallback) endpoints for the current public addresses.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="needIPv4">Whether any enabled record needs an IPv4 address.</param>
    /// <param name="needIPv6">Whether any enabled record needs an IPv6 address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detected addresses. Either may be <c>null</c> when every endpoint failed.</returns>
    public async Task<DetectedIP> DetectAsync(
        PluginConfiguration config,
        bool needIPv4,
        bool needIPv6,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? v4 = null;
        string? v6 = null;
        string? v4Note = null;
        string? v6Note = null;

        // When the user has turned off internal address skipping, accept whatever detection returns.
        var requirePublic = config.SkipInternalAddresses;
        var timeout = config.RequestTimeout();

        if (needIPv4)
        {
            (v4, v4Note) = await DetectFamilyAsync(
                BuildEndpoints(config.IPv4DetectionUrl, PluginConfiguration.DefaultIPv4Endpoints),
                AddressFamily.InterNetwork,
                requirePublic,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }

        if (needIPv6)
        {
            (v6, v6Note) = await DetectFamilyAsync(
                BuildEndpoints(config.IPv6DetectionUrl, PluginConfiguration.DefaultIPv6Endpoints),
                AddressFamily.InterNetworkV6,
                requirePublic,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }

        return new DetectedIP { IPv4 = v4, IPv6 = v6, IPv4Note = v4Note, IPv6Note = v6Note };
    }

    /// <summary>
    /// Builds the ordered, de-duplicated endpoint list for a family from exactly what the administrator
    /// configured (one per line, comma, or space). Nothing is hidden or appended. A blank field falls back
    /// to the built-in defaults so detection still works when every endpoint was removed.
    /// </summary>
    private static List<string> BuildEndpoints(string? configured, string[] defaults)
    {
        var endpoints = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string candidate)
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                endpoints.Add(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var part in configured.Split(EndpointSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                Add(part);
            }
        }

        if (endpoints.Count == 0)
        {
            foreach (var fallback in defaults)
            {
                Add(fallback);
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Queries the endpoints until two agree on the same address, which is then accepted. If only one
    /// endpoint ever returns a usable address, that single answer is trusted. When two or more endpoints
    /// answer but disagree, nothing is published. Returns a note describing any failure.
    /// </summary>
    /// <param name="endpoints">The ordered endpoints to query.</param>
    /// <param name="expected">The address family the endpoints should return.</param>
    /// <param name="requirePublic">Whether an internal or reserved address is rejected rather than accepted.</param>
    /// <param name="timeout">How long a single endpoint is given before moving on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The agreed address, or a null address with a note describing the failure.</returns>
    private async Task<(string? Address, string? Note)> DetectFamilyAsync(
        List<string> endpoints,
        AddressFamily expected,
        bool requirePublic,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var family = expected == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sawInternal = false;
        var responders = 0;
        string? lastValid = null;

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (address, wasInternal) = await ProbeAsync(endpoint, expected, requirePublic, timeout, cancellationToken).ConfigureAwait(false);
            if (address is null)
            {
                sawInternal |= wasInternal;
                continue;
            }

            responders++;
            lastValid = address;
            var seen = counts.GetValueOrDefault(address) + 1;
            counts[address] = seen;
            if (seen >= 2)
            {
                Recovered(family);
                return (address, null);
            }
        }

        // No two endpoints agreed.
        if (responders == 1)
        {
            // Only one endpoint answered, so there is nothing to cross check it against. Trust it.
            Recovered(family);
            return (lastValid, null);
        }

        // Detection runs on every update, so a lasting fault warns once and says so when it clears.
        var problem = _problems.GetOrAdd(family, _ => new RepeatedProblem());

        if (responders >= 2)
        {
            var disagree = "Public " + family + " detection endpoints disagreed on the address, so nothing "
                + "was published. Remove an unreliable endpoint or add a trusted one so two can agree.";
            problem.Failed(level => _logger.Log(level, "Public {Family} detection endpoints disagreed across {Count} endpoint(s).", family, endpoints.Count));
            return (null, disagree);
        }

        if (sawInternal)
        {
            // Detection reached an endpoint and got an answer, but it was a private or reserved address.
            // The server most likely has no public address of this family, so nothing can be published.
            var note = "The " + family + " address returned by detection looks internal or private, so no "
                + family + " records were updated. This usually means the server has no public " + family
                + " address, for example when it sits behind CGNAT or a router whose public side the "
                + "detection endpoint cannot see.";
            problem.Failed(level => _logger.Log(level, "Public {Family} detection saw only internal addresses across {Count} endpoint(s).", family, endpoints.Count));
            return (null, note);
        }

        var failure = "Public " + family + " detection failed. None of the configured or fallback endpoints "
            + "returned a usable address.";
        problem.Failed(level => _logger.Log(level, "Public {Family} detection failed across {Count} endpoint(s).", family, endpoints.Count));
        return (null, failure);
    }

    private void Recovered(string family)
    {
        if (_problems.TryGetValue(family, out var problem))
        {
            problem.Succeeded(() => _logger.LogInformation("Public {Family} detection works again", family));
        }
    }

    private LogLevel WarnOnce(string url) => _warnedEndpoints.TryAdd(url, 0) ? LogLevel.Warning : LogLevel.Debug;

    /// <summary>
    /// Whether a reply is an address written out in full. The parser also takes shorthand such as 8.8, which
    /// means 8.0.0.8, and IPv6 scope ids, neither of which a detection service would ever mean.
    /// </summary>
    /// <param name="body">The reply.</param>
    /// <param name="expected">The address family wanted.</param>
    /// <returns>True for four dotted numbers, or an IPv6 address without a scope id.</returns>
    internal static bool IsPlainAddress(string body, AddressFamily expected)
    {
        if (expected == AddressFamily.InterNetworkV6)
        {
            return body.Length > 0 && body.All(c => Uri.IsHexDigit(c) || c is ':' or '.');
        }

        var parts = body.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// Probes a single https endpoint for the external IP address, bounded by the request timeout.
    /// </summary>
    /// <param name="url">The endpoint to query.</param>
    /// <param name="expected">The address family the endpoint should return.</param>
    /// <param name="requirePublic">Whether an internal or reserved address is rejected rather than accepted.</param>
    /// <param name="requestTimeout">How long the request is given before it is abandoned.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The usable address, otherwise null and a flag marking a rejected internal address.</returns>
    private async Task<(string? Address, bool WasInternal)> ProbeAsync(string url, AddressFamily expected, bool requirePublic, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // A plain http reply could be tampered with, which would point your DNS at an attacker. Refuse it.
            // A setting, the same every run: warned once.
            _logger.Log(WarnOnce(url), "Refusing the non-https IP detection endpoint {Url}", url);
            return (null, false);
        }

        // A malformed endpoint fails on its own rather than aborting detection for every other endpoint.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            _logger.Log(WarnOnce(url), "Skipping the malformed IP detection endpoint {Url}", url);
            return (null, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var body = (await ReadBoundedAsync(client, uri, timeout.Token).ConfigureAwait(false)).Trim();

            if (!IsPlainAddress(body, expected) || !IPAddress.TryParse(body, out var parsed) || parsed.AddressFamily != expected || parsed.IsIPv4MappedToIPv6)
            {
                _logger.LogDebug("IP detection endpoint {Url} returned an unexpected value", url);
                return (null, false);
            }

            // Reject internal addresses only when the user wants that validation. With it off, an internal
            // or CGNAT address is accepted and published as is.
            if (requirePublic && !IPAddressClassifier.IsPublic(parsed))
            {
                _logger.LogDebug("IP detection endpoint {Url} returned a non-public address {Address}", url, parsed);
                return (null, true);
            }

            return (parsed.ToString(), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or IOException)
        {
            _logger.LogDebug(ex, "IP detection request to {Url} failed", url);
            return (null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per probe timeout fired rather than an outer cancellation. Fail this endpoint and move on.
            _logger.LogDebug("IP detection request to {Url} timed out", url);
            return (null, false);
        }
    }

    // Reads at most MaxResponseChars of a successful response body. A non success status throws, so the
    // endpoint is failed over like any other error.
    private static async Task<string> ReadBoundedAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxResponseChars];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return new string(buffer, 0, total);
        }
    }
}
