using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// An immutable snapshot of everything the running proxy needs. Settings changes build a new snapshot and
/// swap it in, so a request always sees one consistent configuration.
/// </summary>
public sealed class ProxyRuntime
{
    /// <summary>The YARP route and cluster id for Jellyfin.</summary>
    public const string JellyfinId = "jellyfin";

    /// <summary>The route and cluster id for Jellyfin's media streams, which a paused player may leave idle.</summary>
    public const string JellyfinMediaId = "jellyfin-media";

    /// <summary>Gets the listener options.</summary>
    public required ListenerOptions Listener { get; init; }

    /// <summary>Gets the public hostnames that reach Jellyfin.</summary>
    public required IReadOnlyList<string> JellyfinHostnames { get; init; }

    /// <summary>Gets the enabled external routes.</summary>
    public required IReadOnlyList<RuntimeRoute> Routes { get; init; }

    /// <summary>Gets Jellyfin's address on this machine.</summary>
    public required Uri JellyfinBackend { get; init; }

    /// <summary>Gets Jellyfin's base URL, empty or like <c>/jellyfin</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Gets a value indicating whether Jellyfin honours the proxy's forwarding headers. When it does not,
    /// every proxied client looks local to Jellyfin, so internet requests for Jellyfin are refused.
    /// </summary>
    public required bool JellyfinTrustsForwarding { get; init; }

    /// <summary>Gets the resolved options: the administrator's changes over the built-in defaults.</summary>
    public required ProxyOptions Options { get; init; }

    /// <summary>Gets the LAN classifier.</summary>
    public required NetworkClassifier Classifier { get; init; }

    /// <summary>Gets the internet addresses that are always refused.</summary>
    public IReadOnlyList<IPNetwork> BlockedNetworks { get; init; } = Array.Empty<IPNetwork>();

    /// <summary>Gets Jellyfin's and the proxy's ports, which no Service may reach on this machine.</summary>
    public IReadOnlyList<int> ReservedPorts { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Whether a host is one the proxy was configured to serve, either for Jellyfin or a route.
    /// </summary>
    /// <param name="host">The request host, without port.</param>
    /// <returns>True for configured hostnames.</returns>
    public bool IsConfiguredHost(string? host)
        => HostnameMatcher.MatchesAny(host, JellyfinHostnames) || Routes.Any(r => HostnameMatcher.MatchesAny(host, r.Hostnames));

    /// <summary>
    /// Finds the external route a host belongs to. Null means the request goes to Jellyfin.
    /// </summary>
    /// <param name="host">The request host, without port.</param>
    /// <returns>The route, or null for Jellyfin.</returns>
    public RuntimeRoute? MatchRoute(string? host)
    {
        // Exact names win over wildcards, Jellyfin's included, so a route's *.example.com never captures
        // Jellyfin's own media.example.com.
        var exact = Routes.FirstOrDefault(r => r.Hostnames.Any(h => !h.StartsWith('*') && HostnameMatcher.Matches(host, h)));
        if (exact is not null)
        {
            return exact;
        }

        if (JellyfinHostnames.Any(h => !h.StartsWith('*') && HostnameMatcher.Matches(host, h)))
        {
            return null;
        }

        return Routes.FirstOrDefault(r => HostnameMatcher.MatchesAny(host, r.Hostnames));
    }

    /// <summary>
    /// Every hostname the proxy serves, for the certificate and the status page.
    /// </summary>
    /// <returns>The distinct hostnames.</returns>
    public IReadOnlyList<string> AllHostnames()
        => JellyfinHostnames.Concat(Routes.SelectMany(r => r.Hostnames)).Distinct(StringComparer.Ordinal).ToList();
}
