using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// An internet address the block list or country rules refused, with how often and when.
/// </summary>
public sealed class BlockedAttempt
{
    /// <summary>Gets or sets the address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the two-letter country code, empty when unknown.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name, empty when unknown.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets what refused it.</summary>
    public BlockReason Reason { get; set; }

    /// <summary>Gets or sets how many connections were refused.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the first refusal (UTC).</summary>
    public DateTime FirstUtc { get; set; }

    /// <summary>Gets or sets the latest refusal (UTC).</summary>
    public DateTime LastUtc { get; set; }
}
