using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Single configuration object for the plugin. XML-serialized by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the DNS records kept in sync with the server's public IP.</summary>
    public List<DnsRecord> Records { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether IPv4 (A records) is detected and updated.</summary>
    public bool EnableIPv4 { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether IPv6 (AAAA records) is detected and updated.</summary>
    public bool EnableIPv6 { get; set; }

    /// <summary>Gets or sets the URL queried to discover the current public IPv4 address.</summary>
    public string IPv4DetectionUrl { get; set; } = "https://api.ipify.org";

    /// <summary>Gets or sets the URL queried to discover the current public IPv6 address.</summary>
    public string IPv6DetectionUrl { get; set; } = "https://api6.ipify.org";

    /// <summary>Gets or sets the public IPv4 address seen on the most recent update run (read-only in the UI).</summary>
    public string LastDetectedIPv4 { get; set; } = string.Empty;

    /// <summary>Gets or sets the public IPv6 address seen on the most recent update run (read-only in the UI).</summary>
    public string LastDetectedIPv6 { get; set; } = string.Empty;
}
