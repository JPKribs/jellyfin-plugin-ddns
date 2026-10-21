using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// One row of the Remote Access table: an address and what it did.
/// </summary>
public sealed class ActivityRow
{
    /// <summary>Gets or sets what the address did.</summary>
    public ActivityType Type { get; set; }

    /// <summary>Gets or sets the user, or the username tried for a failure; empty otherwise.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets the address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the device or browser, when known.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets the address it used to reach Jellyfin, when known.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the two-letter country code, empty when unknown.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name, empty when unknown.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the city, when the location database has city detail; empty otherwise.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the address is on the local network.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets when it was last seen (UTC).</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>Gets or sets how many times: failed sign-ins, refused connections or requests.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets what refused it, for a blocked address.</summary>
    public BlockReason? Reason { get; set; }

    /// <summary>Gets or sets a value indicating whether the address itself is on the block list.</summary>
    public bool Listed { get; set; }

    /// <summary>Gets or sets a value indicating whether a block list entry covers the address.</summary>
    public bool Covered { get; set; }

    /// <summary>Gets or sets a value indicating whether the address is banned for failed sign-ins.</summary>
    public bool Banned { get; set; }

    /// <summary>Gets or sets a value indicating whether the country rules refuse its country.</summary>
    public bool CountryBlocked { get; set; }

    /// <summary>Gets or sets a value indicating whether blocking never applies to it: the local or a trusted network.</summary>
    public bool Exempt { get; set; }
}
