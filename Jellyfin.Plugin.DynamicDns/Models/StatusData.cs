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

    /// <summary>Gets or sets the number of consecutive runs where a needed public IP could not be detected.</summary>
    public int ConsecutiveDetectionFailures { get; set; }

    /// <summary>Gets or sets a value indicating whether the unhealthy detection warning has been written to the activity log for the current failure streak.</summary>
    public bool DetectionUnhealthyLogged { get; set; }

    /// <summary>Gets or sets a value indicating whether the plaintext credential warning has been written to the activity log for the current encryption outage.</summary>
    public bool PlaintextWarningLogged { get; set; }

    /// <summary>Gets or sets the per record status, keyed by record id.</summary>
    public Dictionary<string, RecordStatus> Records { get; set; } = new(StringComparer.Ordinal);
}
