using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Classifies an IP address as public or internal (private, loopback, link local, CGNAT, or otherwise
/// reserved). Shared by detection and the update decision so both judge an address the same way.
/// </summary>
public static class IPAddressClassifier
{
    /// <summary>Returns whether the address is a public, globally routable address.</summary>
    /// <param name="address">The address to classify.</param>
    /// <returns>True when the address is public, false when it is internal or reserved.</returns>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes switch
            {
                [0, ..] => false,
                [10, ..] => false,
                [100, >= 64 and <= 127, ..] => false,
                [127, ..] => false,
                [169, 254, ..] => false,
                [172, >= 16 and <= 31, ..] => false,
                [192, 0, 0, ..] => false,
                [192, 0, 2, ..] => false,
                [192, 88, 99, ..] => false,
                [192, 168, ..] => false,
                [198, 18 or 19, ..] => false,
                [198, 51, 100, ..] => false,
                [203, 0, 113, ..] => false,
                [>= 224, ..] => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return bytes switch
            {
                // Unspecified, loopback and the deprecated IPv4-compatible block.
                [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ..] => false,
                [0xFE, >= 0x80, ..] => false,
                [0xFC or 0xFD, ..] => false,
                [0xFF, ..] => false,

                // Discard, documentation, benchmarking, ORCHID and Teredo: never a host's own stable address.
                [0x01, 0x00, 0, 0, 0, 0, 0, 0, ..] => false,
                [0x20, 0x01, 0x0D, 0xB8, ..] => false,
                [0x3F, 0xFF, < 0x10, ..] => false,
                [0x20, 0x01, 0x00, 0x02, 0x00, 0x00, ..] => false,
                [0x20, 0x01, 0x00, >= 0x10 and <= 0x2F, ..] => false,
                [0x20, 0x01, 0x00, 0x00, ..] => false,

                // NAT64 local use, and the forms that carry an IPv4 address: as public as the address inside.
                [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01, ..] => false,
                [0x00, 0x64, 0xFF, 0x9B, 0, 0, 0, 0, 0, 0, 0, 0, ..] or [0x20, 0x02, ..] => IsPublic(EmbeddedIPv4(address)!),

                // Only 2000::/3 is handed out as global unicast; anything else is not yet anyone's address.
                [>= 0x20 and <= 0x3F, ..] => true,
                _ => false,
            };
        }

        return false;
    }

    /// <summary>
    /// The IPv4 address an IPv6 one carries, which a translator or tunnel would reach: NAT64 (the well-known and
    /// local-use prefixes), the deprecated IPv4-compatible form, and 6to4.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The IPv4 address inside, or null when there is none.</returns>
    public static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return null;
        }

        var b = address.GetAddressBytes();
        if ((b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) || b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            return new IPAddress(b[12..16]);
        }

        return b[0] == 0x20 && b[1] == 0x02 ? new IPAddress(b[2..6]) : null;
    }
}
