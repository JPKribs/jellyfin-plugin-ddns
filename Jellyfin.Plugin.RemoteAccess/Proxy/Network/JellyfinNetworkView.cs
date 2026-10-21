using System;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// Jellyfin's network setup as it is running now. Jellyfin reads its ports, bind addresses, base URL and
/// trusted proxies once at startup, so these are captured when the plugin starts rather than read from the
/// saved configuration, which may hold changes that wait for a restart.
/// </summary>
/// <param name="Backend">Jellyfin's address on this machine.</param>
/// <param name="BaseUrl">Jellyfin's base URL, empty or like <c>/jellyfin</c>.</param>
/// <param name="TrustsForwarding">Whether Jellyfin honours forwarding headers from the proxy.</param>
/// <param name="HttpPort">Jellyfin's HTTP port.</param>
/// <param name="HttpsPort">Jellyfin's HTTPS port, or zero when it does not serve HTTPS.</param>
public sealed record JellyfinNetworkView(Uri Backend, string BaseUrl, bool TrustsForwarding, int HttpPort, int HttpsPort)
{
    /// <summary>
    /// Builds the view from the configuration Jellyfin started with and the ports it is actually using.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration.</param>
    /// <param name="httpPort">The HTTP port Jellyfin is listening on.</param>
    /// <param name="httpsPort">The HTTPS port, or zero when Jellyfin does not serve HTTPS.</param>
    /// <returns>The view.</returns>
    public static JellyfinNetworkView From(NetworkConfiguration network, int httpPort, int httpsPort)
    {
        ArgumentNullException.ThrowIfNull(network);
        var backend = NetworkTakeover.BackendUri(network);
        backend = new UriBuilder(backend) { Port = httpPort }.Uri;
        return new JellyfinNetworkView(backend, network.BaseUrl ?? string.Empty, NetworkTakeover.TrustsForwarding(network), httpPort, httpsPort);
    }
}
