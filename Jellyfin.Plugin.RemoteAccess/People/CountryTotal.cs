using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// How much came from one country, for the People map.
/// </summary>
public sealed class CountryTotal
{
    /// <summary>Gets or sets the two-letter country code.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the count: people seen, or failed sign-ins.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets when the latest one happened (UTC).</summary>
    public DateTime LastUtc { get; set; }
}
