namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Who may reach a route.
/// </summary>
public enum RouteExposure
{
    /// <summary>Reachable from the LAN and the internet.</summary>
    LanAndWan,

    /// <summary>Reachable from the LAN and trusted networks only.</summary>
    LanOnly
}
