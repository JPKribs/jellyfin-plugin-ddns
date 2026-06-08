using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IPDetectionService> _logger;

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
        var timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds > 0 ? config.RequestTimeoutSeconds : 15);

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
                return (address, null);
            }
        }

        // No two endpoints agreed.
        if (responders == 1)
        {
            // Only one endpoint answered, so there is nothing to cross check it against. Trust it.
            return (lastValid, null);
        }

        if (responders >= 2)
        {
            var disagree = "Public " + family + " detection endpoints disagreed on the address, so nothing "
                + "was published. Remove an unreliable endpoint or add a trusted one so two can agree.";
            _logger.LogWarning("Public {Family} detection endpoints disagreed across {Count} endpoint(s).", family, endpoints.Count);
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
            _logger.LogWarning("Public {Family} detection saw only internal addresses across {Count} endpoint(s).", family, endpoints.Count);
            return (null, note);
        }

        var failure = "Public " + family + " detection failed. None of the configured or fallback endpoints "
            + "returned a usable address.";
        _logger.LogWarning("Public {Family} detection failed across {Count} endpoint(s).", family, endpoints.Count);
        return (null, failure);
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
            _logger.LogWarning("Refusing the non-https IP detection endpoint {Url}", url);
            return (null, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var body = (await client.GetStringAsync(url, timeout.Token).ConfigureAwait(false)).Trim();

            if (!IPAddress.TryParse(body, out var parsed) || parsed.AddressFamily != expected)
            {
                _logger.LogWarning("IP detection endpoint {Url} returned an unexpected value", url);
                return (null, false);
            }

            // Reject internal addresses only when the user wants that validation. With it off, an internal
            // or CGNAT address is accepted and published as is.
            if (requirePublic && !IPAddressClassifier.IsPublic(parsed))
            {
                _logger.LogWarning("IP detection endpoint {Url} returned a non-public address {Address}", url, parsed);
                return (null, true);
            }

            return (parsed.ToString(), false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "IP detection request to {Url} failed", url);
            return (null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per probe timeout fired rather than an outer cancellation. Fail this endpoint and move on.
            _logger.LogWarning("IP detection request to {Url} timed out", url);
            return (null, false);
        }
    }
}
