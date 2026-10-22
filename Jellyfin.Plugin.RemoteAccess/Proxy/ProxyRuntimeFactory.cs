using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Turns saved settings and Jellyfin's network configuration into a runtime snapshot.
/// </summary>
public static class ProxyRuntimeFactory
{
    /// <summary>
    /// Deep-copies settings, so a snapshot never shares objects with the live configuration.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>An independent copy.</returns>
    public static ProxySettings Clone(ProxySettings? settings)
        => settings is null
            ? new ProxySettings()
            : JsonSerializer.Deserialize<ProxySettings>(JsonSerializer.Serialize(settings)) ?? new ProxySettings();

    /// <summary>
    /// Builds a runtime snapshot.
    /// </summary>
    /// <param name="settings">The proxy settings (normalized).</param>
    /// <param name="jellyfin">Jellyfin's running network setup.</param>
    /// <param name="isLan">Jellyfin's LAN test.</param>
    /// <param name="httpsAvailable">Whether a certificate is loaded, which decides whether the HTTPS listener runs.</param>
    /// <param name="probe">Whether to build a throwaway probe: HTTP only, on a free loopback port.</param>
    /// <returns>The runtime.</returns>
    public static ProxyRuntime Build(
        ProxySettings settings,
        JellyfinNetworkView jellyfin,
        Func<IPAddress, bool> isLan,
        bool httpsAvailable,
        bool probe = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(jellyfin);
        var options = ProxyOptions.From(settings.Advanced);
        var listener = new ListenerOptions(
            probe ? 0 : options.HttpPort,
            probe || !httpsAvailable ? null : options.HttpsPort,
            probe,
            options.EnableHttp2,
            options.KeepAliveSeconds,
            options.RequestHeadersTimeoutSeconds,
            options.MaxRequestHeadersKb,
            options.MaxConnections);

        return new ProxyRuntime
        {
            Listener = listener,
            JellyfinHostnames = settings.Hostnames.ToList(),
            JellyfinBackend = jellyfin.Backend,
            BaseUrl = jellyfin.BaseUrl,
            JellyfinTrustsForwarding = jellyfin.TrustsForwarding,
            Options = options,
            Classifier = new NetworkClassifier(isLan, NetworkClassifier.ParseNetworks(options.TrustedNetworks)),
            BlockedNetworks = NetworkClassifier.ParseNetworks(options.BlockedAddresses)
        };
    }
}
