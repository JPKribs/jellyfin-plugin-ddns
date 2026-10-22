using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Settings for the built-in reverse proxy that sits in front of Jellyfin. Everything is off until
/// <see cref="Enabled"/> is set, so a plain Dynamic DNS install behaves exactly as before.
/// </summary>
public class ProxySettings
{
    /// <summary>Gets or sets a value indicating whether the proxy runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the public hostnames that reach Jellyfin, for example <c>media.example.com</c> or
    /// <c>*.example.com</c>. Requests from the internet for any other name are dropped.
    /// </summary>
    public List<string> Hostnames { get; set; } = new();

    /// <summary>Gets or sets where the TLS certificate comes from.</summary>
    public ProxyCertificateSettings Certificate { get; set; } = new();

    /// <summary>
    /// Gets or sets the advanced values the administrator changed. Anything left empty follows the built-in
    /// default in <see cref="ProxyOptions.Defaults"/>, so better defaults in a later version apply on their own.
    /// </summary>
    public ProxyAdvancedSettings Advanced { get; set; } = new();
}
