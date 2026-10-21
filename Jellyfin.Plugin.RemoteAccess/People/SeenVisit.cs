using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// An internet address that reached Jellyfin through the proxy, whether or not it went on to sign in.
/// </summary>
public sealed class SeenVisit
{
    /// <summary>Gets or sets the address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the address it used, like <c>https://media.example.com:8920</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets a short description of its browser or app.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets the two-letter country code, empty when unknown.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name, empty when unknown.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets how many requests it made.</summary>
    public int Requests { get; set; }

    /// <summary>Gets or sets the first request (UTC).</summary>
    public DateTime FirstUtc { get; set; }

    /// <summary>Gets or sets the latest request (UTC).</summary>
    public DateTime LastUtc { get; set; }
}
