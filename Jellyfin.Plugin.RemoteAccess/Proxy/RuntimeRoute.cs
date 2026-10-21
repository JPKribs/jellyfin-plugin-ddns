using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// An enabled external route as the running proxy sees it.
/// </summary>
/// <param name="Id">The route id, also the YARP route and cluster id suffix.</param>
/// <param name="Name">The friendly name.</param>
/// <param name="Hostnames">The normalized hostnames.</param>
/// <param name="Target">The backend URL.</param>
/// <param name="Exposure">Who may reach it.</param>
/// <param name="PreserveHost">Whether the client's Host header is forwarded.</param>
/// <param name="BackendTls">How the backend certificate is checked.</param>
/// <param name="PinnedThumbprint">The pinned SHA-256 thumbprint, upper-case hex.</param>
/// <param name="Overrides">Values that replace the global settings for this route.</param>
public sealed record RuntimeRoute(
    string Id,
    string Name,
    IReadOnlyList<string> Hostnames,
    Uri Target,
    RouteExposure Exposure,
    bool PreserveHost,
    BackendTlsMode BackendTls,
    string PinnedThumbprint,
    RouteOverrides Overrides)
{
    /// <summary>Gets the YARP cluster id.</summary>
    public string ClusterId => "route-" + Id;
}
