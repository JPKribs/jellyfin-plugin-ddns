using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// A sign-in attempt.
/// </summary>
public sealed class SignInAttempt
{
    /// <summary>Gets or sets when it happened (UTC).</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>Gets or sets the username tried, empty when Jellyfin did not report it.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user id, empty when no such user exists.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the client IP address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the hostname the client asked for.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the app name.</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the address is on the LAN or a trusted network.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets the country code.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the country name.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the city name.</summary>
    public string City { get; set; } = string.Empty;
}
