using System.Collections.Generic;
using System.Xml.Serialization;
using Jellyfin.Plugin.DynamicDns.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DynamicDns.Configuration;

/// <summary>
/// Single configuration object for the plugin. XML-serialized by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The full default IPv4 detection endpoints. Seeded into the config so every endpoint is visible and editable.</summary>
    public static readonly string[] DefaultIPv4Endpoints =
    {
        "https://api.ipify.org",
        "https://ipv4.icanhazip.com",
        "https://checkip.amazonaws.com",
        "https://v4.ident.me"
    };

    /// <summary>The full default IPv6 detection endpoints. Seeded into the config so every endpoint is visible and editable.</summary>
    public static readonly string[] DefaultIPv6Endpoints =
    {
        "https://api6.ipify.org",
        "https://ipv6.icanhazip.com",
        "https://v6.ident.me"
    };

    /// <summary>Gets or sets the DNS records kept in sync with the server's public IP.</summary>
    public List<DNSRecord> Records { get; set; } = new();

    /// <summary>Gets or sets the endpoints queried to discover the current public IPv4 address, one per line.</summary>
    public string IPv4DetectionUrl { get; set; } = string.Join("\n", DefaultIPv4Endpoints);

    /// <summary>Gets or sets the endpoints queried to discover the current public IPv6 address, one per line.</summary>
    public string IPv6DetectionUrl { get; set; } = string.Join("\n", DefaultIPv6Endpoints);

    /// <summary>
    /// Gets or sets how many hours may pass before a record is re-pushed to its provider even when the IP
    /// has not changed. Zero disables this and keeps the default behavior of skipping unchanged records.
    /// </summary>
    public int ForceUpdateHours { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a detected address in a private, loopback, link local,
    /// CGNAT, or other reserved range is rejected rather than published. True by default. Turn it off to
    /// publish whatever detection returns even when it looks internal.
    /// </summary>
    public bool SkipInternalAddresses { get; set; } = true;

    /// <summary>
    /// Gets or sets how many consecutive failures pause a record before it is retried, to avoid hammering a
    /// provider with a misconfigured record. Zero disables backoff. Three by default.
    /// </summary>
    public int BackoffAfterFailures { get; set; } = 3;

    /// <summary>Gets or sets how many hours a paused record waits before it is retried once. Twenty four by default.</summary>
    public int BackoffHours { get; set; } = 24;

    /// <summary>Gets or sets how many seconds an outbound detection or provider request may take. Fifteen by default.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    // The detected IP and detection warning are runtime status, kept in the separate status store rather
    // than the config XML, so they carry [XmlIgnore]. They still serialize to the JSON API for the UI and
    // are populated from the store by the controller.

    /// <summary>Gets or sets the public IPv4 address seen on the most recent update run (read-only in the UI).</summary>
    [XmlIgnore]
    public string LastDetectedIPv4 { get; set; } = string.Empty;

    /// <summary>Gets or sets the public IPv6 address seen on the most recent update run (read-only in the UI).</summary>
    [XmlIgnore]
    public string LastDetectedIPv6 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a warning from the most recent run when a wanted IP family could not be resolved to a
    /// public address, for example when the detected address looked internal. Empty when detection was
    /// clean. Surfaced read only on the Address tab.
    /// </summary>
    [XmlIgnore]
    public string LastDetectionMessage { get; set; } = string.Empty;
}
