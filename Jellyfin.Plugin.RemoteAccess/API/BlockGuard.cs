using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// Which addresses blocking can apply to, and which block list entries name one address.
/// </summary>
internal static class BlockGuard
{
    /// <summary>
    /// Whether blocking never applies to an address: loopback, the LAN, or a trusted network.
    /// </summary>
    /// <param name="ip">The normalized address.</param>
    /// <param name="network">Jellyfin's network manager, for the LAN test.</param>
    /// <param name="trusted">The trusted networks.</param>
    /// <returns>True when the address is exempt.</returns>
    public static bool IsExempt(IPAddress ip, INetworkManager network, IEnumerable<IPNetwork> trusted)
        => IPAddress.IsLoopback(ip) || network.IsInLocalNetwork(ip) || trusted.Any(n => n.Contains(ip));

    /// <summary>
    /// Whether a block list entry names exactly one address, written bare or as a full-length block.
    /// </summary>
    /// <param name="entry">The block list entry.</param>
    /// <param name="ip">The normalized address.</param>
    /// <returns>True when the entry is that address alone.</returns>
    public static bool NamesOnly(string entry, IPAddress ip)
        => NamesOnly(entry, Single(ip));

    /// <summary>
    /// Whether a block list entry names exactly this network, such as an IPv6 /64 the history keeps a visitor as.
    /// </summary>
    /// <param name="entry">The block list entry.</param>
    /// <param name="target">The network.</param>
    /// <returns>True when the entry is that network alone.</returns>
    public static bool NamesOnly(string entry, IPNetwork target)
        => NetworkClassifier.TryParseNetwork(entry, out var parsed)
            && Unmapped(parsed) is var n
            && n.BaseAddress.Equals(target.BaseAddress)
            && n.PrefixLength == target.PrefixLength;

    // An entry written as an IPv4-mapped IPv6 address, such as ::ffff:203.0.113.5, names the IPv4 address that
    // the proxy compares it as.
    private static IPNetwork Unmapped(IPNetwork n)
        => n.BaseAddress.IsIPv4MappedToIPv6 && n.PrefixLength >= 96
            ? new IPNetwork(n.BaseAddress.MapToIPv4(), n.PrefixLength - 96)
            : n;

    /// <summary>
    /// Reads what the Remote Access page asks to block: one address, or an IPv6 /64 as its history shows it.
    /// </summary>
    /// <param name="text">The address or network.</param>
    /// <param name="target">The network, a single address as a full-length one.</param>
    /// <returns>True when the text is one of those.</returns>
    public static bool TryParseTarget(string? text, out IPNetwork target)
    {
        target = default;
        if (IPAddress.TryParse(text, out var ip))
        {
            target = Single(NetworkClassifier.Normalize(ip));
            return true;
        }

        if (NetworkClassifier.TryParseNetwork(text ?? string.Empty, out var network)
            && network.PrefixLength == 64
            && network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            target = network;
            return true;
        }

        return false;
    }

    /// <summary>
    /// How a target is written in the block list: a bare address, or the network.
    /// </summary>
    /// <param name="target">The network.</param>
    /// <returns>The text.</returns>
    public static string Text(IPNetwork target)
        => target.PrefixLength == (target.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
            ? target.BaseAddress.ToString()
            : target.ToString();

    private static IPNetwork Single(IPAddress ip)
        => new(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
}
