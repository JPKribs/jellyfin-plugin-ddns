using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// A route from one or more hostnames to another service, sharing the proxy's port and certificate.
/// </summary>
public class ProxyRoute
{
    /// <summary>Gets or sets the stable identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets a friendly name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the route is served.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the hostnames this route answers, for example <c>requests.example.com</c>.</summary>
    public List<string> Hostnames { get; set; } = new();

    /// <summary>Gets or sets the backend URL, for example <c>http://192.168.1.20:5055</c>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Gets or sets who may reach the route.</summary>
    public RouteExposure Exposure { get; set; } = RouteExposure.LanAndWan;

    /// <summary>Gets or sets a value indicating whether the client's Host header is sent to the backend instead of the backend's own.</summary>
    public bool PreserveHost { get; set; }

    /// <summary>Gets or sets how an HTTPS backend's certificate is checked.</summary>
    public BackendTlsMode BackendTls { get; set; } = BackendTlsMode.Verify;

    /// <summary>Gets or sets the SHA-256 thumbprint accepted in <see cref="BackendTlsMode.Pinned"/> mode.</summary>
    public string PinnedThumbprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the values that replace the global settings for this route.</summary>
    public RouteOverrides Overrides { get; set; } = new();
}
