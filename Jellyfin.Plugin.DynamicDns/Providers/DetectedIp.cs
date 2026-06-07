namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// The public IP addresses discovered for the server.
/// </summary>
public sealed class DetectedIp
{
    /// <summary>Gets the detected public IPv4 address, or <c>null</c> when none was found.</summary>
    public string? IPv4 { get; init; }

    /// <summary>Gets the detected public IPv6 address, or <c>null</c> when none was found.</summary>
    public string? IPv6 { get; init; }

    /// <summary>Gets a value indicating whether at least one address was detected.</summary>
    public bool HasAny => IPv4 is not null || IPv6 is not null;
}
