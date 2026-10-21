using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// An internet address banned for too many failed sign-ins.
/// </summary>
public sealed class Ban
{
    /// <summary>Gets or sets the address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the two-letter country code, empty when unknown.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name, empty when unknown.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets how many failed sign-ins led to the ban.</summary>
    public int Failures { get; set; }

    /// <summary>Gets or sets when the ban started (UTC).</summary>
    public DateTime BannedUtc { get; set; }

    /// <summary>Gets or sets when the ban ends (UTC), or null when it lasts until it is lifted.</summary>
    public DateTime? UntilUtc { get; set; }

    /// <summary>
    /// Whether the ban is still in force.
    /// </summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>True while it lasts.</returns>
    public bool ActiveAt(DateTime now) => UntilUtc is null || UntilUtc > now;
}
