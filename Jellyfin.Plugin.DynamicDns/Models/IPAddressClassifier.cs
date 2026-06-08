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
                [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] => false,
                [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1] => false,
                [0xFE, >= 0x80, ..] => false,
                [0xFC or 0xFD, ..] => false,
                [0xFF, ..] => false,
                [0x20, 0x01, 0x0D, 0xB8, ..] => false,
                _ => true,
            };
        }

        return false;
    }
}
