namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The public IP addresses discovered for the server, plus a note for any family that was wanted but
/// could not be resolved to a usable public address.
/// </summary>
public sealed class DetectedIP
{
    /// <summary>Gets the detected public IPv4 address, or <c>null</c> when none was found.</summary>
    public string? IPv4 { get; init; }

    /// <summary>Gets the detected public IPv6 address, or <c>null</c> when none was found.</summary>
    public string? IPv6 { get; init; }

    /// <summary>
    /// Gets a human readable note explaining why IPv4 detection produced no address, or <c>null</c> when
    /// IPv4 was found or was not requested. Set when a detected address looked internal.
    /// </summary>
    public string? IPv4Note { get; init; }

    /// <summary>
    /// Gets a human readable note explaining why IPv6 detection produced no address, or <c>null</c> when
    /// IPv6 was found or was not requested. Set when a detected address looked internal.
    /// </summary>
    public string? IPv6Note { get; init; }

    /// <summary>Gets a value indicating whether at least one address was detected.</summary>
    public bool HasAny => IPv4 is not null || IPv6 is not null;
}
