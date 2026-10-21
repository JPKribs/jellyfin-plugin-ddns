namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Small derived values for a <see cref="DNSRecord"/>, kept on the model so callers read intent
/// (a display name, which families to push) instead of repeating the same expressions.
/// </summary>
public static class DNSRecordExtensions
{
    /// <summary>Returns the record's friendly name, falling back to the hostname when none is set.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The name, or the hostname when the name is blank.</returns>
    public static string DisplayName(this DNSRecord record)
        => string.IsNullOrWhiteSpace(record.Name) ? record.Hostname : record.Name;

    /// <summary>Returns whether this record should update IPv4 given the detected addresses.</summary>
    /// <param name="record">The record.</param>
    /// <param name="ip">The detected public addresses.</param>
    /// <returns>True when the record updates A records and a public IPv4 was detected.</returns>
    public static bool WantsIPv4(this DNSRecord record, DetectedIP ip)
        => record.UpdateIPv4 && ip.IPv4 is not null;

    /// <summary>Returns whether this record should update IPv6 given the detected addresses.</summary>
    /// <param name="record">The record.</param>
    /// <param name="ip">The detected public addresses.</param>
    /// <returns>True when the record updates AAAA records and a public IPv6 was detected.</returns>
    public static bool WantsIPv6(this DNSRecord record, DetectedIP ip)
        => record.UpdateIPv6 && ip.IPv6 is not null;
}
