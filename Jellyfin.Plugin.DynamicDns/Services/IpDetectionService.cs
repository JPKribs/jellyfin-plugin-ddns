using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Discovers the server's current public IPv4/IPv6 addresses from configurable echo endpoints.
/// </summary>
public sealed class IpDetectionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IpDetectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IpDetectionService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public IpDetectionService(IHttpClientFactory httpClientFactory, ILogger<IpDetectionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Queries the configured endpoints for the current public addresses.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="needIPv4">Whether any enabled record needs an IPv4 address.</param>
    /// <param name="needIPv6">Whether any enabled record needs an IPv6 address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detected addresses; either may be <c>null</c> when detection fails.</returns>
    public async Task<DetectedIp> DetectAsync(
        PluginConfiguration config,
        bool needIPv4,
        bool needIPv6,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? v4 = null;
        string? v6 = null;

        if (needIPv4 && !string.IsNullOrWhiteSpace(config.IPv4DetectionUrl))
        {
            v4 = await ProbeAsync(config.IPv4DetectionUrl, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        }

        if (needIPv6 && !string.IsNullOrWhiteSpace(config.IPv6DetectionUrl))
        {
            v6 = await ProbeAsync(config.IPv6DetectionUrl, AddressFamily.InterNetworkV6, cancellationToken).ConfigureAwait(false);
        }

        return new DetectedIp { IPv4 = v4, IPv6 = v6 };
    }

    /// <summary>
    /// Probe to attempt to retreive the external IP address.
    /// </summary>
    private async Task<string?> ProbeAsync(string url, AddressFamily expected, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var body = (await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();

            if (!IPAddress.TryParse(body, out var parsed) || parsed.AddressFamily != expected)
            {
                _logger.LogWarning("IP detection endpoint {Url} returned an unexpected value", url);
                return null;
            }

            if (!IsPublic(parsed))
            {
                _logger.LogWarning("IP detection endpoint {Url} returned a non-public address {Address}", url, parsed);
                return null;
            }

            return parsed.ToString();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "IP detection request to {Url} failed", url);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("IP detection request to {Url} timed out", url);
            return null;
        }
    }

    /// <summary>
    /// Checks if the IP address is a public (True) or internal (False).
    /// This *should* never be the case but this is a sanity check.
    /// </summary>
    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes switch
            {
                [0, ..] => false,
                [10, ..] => false,
                [100, >= 64 and <= 127, ..] => false,
                [127, ..] => false,
                [169, 254, ..] => false,
                [172, >= 16 and <= 31, ..] => false,
                [192, 0, 0, ..] => false,
                [192, 0, 2, ..] => false,
                [192, 88, 99, ..] => false,
                [192, 168, ..] => false,
                [198, 18 or 19, ..] => false,
                [198, 51, 100, ..] => false,
                [203, 0, 113, ..] => false,
                [>= 224, ..] => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return bytes switch
            {
                [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] => false,
                [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1] => false,
                [0xFE, >= 0x80, ..] => false,
                [0xFC or 0xFD, ..] => false,
                [0xFF, ..] => false,
                [0x20, 0x01, 0x0D, 0xB8, ..] => false,
                _ => true,
            };
        }

        return false;
    }
}