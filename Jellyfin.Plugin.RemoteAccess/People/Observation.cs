using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// One user seen from one address with one device, and the address they used to reach the server.
/// </summary>
public sealed class Observation
{
    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name when last seen.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the client IP address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the hostname the client asked for, empty when it connected directly.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the device id.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the app name.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the address is on the LAN or a trusted network.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets the country code.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the city name.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Gets or sets when this was first seen (UTC).</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when this was last seen (UTC).</summary>
    public DateTime LastSeenUtc { get; set; }
}
