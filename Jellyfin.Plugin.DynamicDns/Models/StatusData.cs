using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// All runtime status the plugin keeps outside its configuration: the most recent detected public IP, the
/// detection warning, and the per record status keyed by record id.
/// </summary>
public sealed class StatusData
{
    /// <summary>Gets or sets the public IPv4 address seen on the most recent run.</summary>
    public string DetectedIPv4 { get; set; } = string.Empty;

    /// <summary>Gets or sets the public IPv6 address seen on the most recent run.</summary>
    public string DetectedIPv6 { get; set; } = string.Empty;

    /// <summary>Gets or sets the detection warning from the most recent run, or empty when detection was clean.</summary>
    public string DetectionMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets the per record status, keyed by record id.</summary>
    public Dictionary<string, RecordStatus> Records { get; set; } = new(StringComparer.Ordinal);
}
