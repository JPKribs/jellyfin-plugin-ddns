using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Decides whether a client address is local (the LAN or a trusted network) or from the internet. The LAN
/// test is Jellyfin's own, so the proxy and Jellyfin agree on what "local" means. The address is always the
/// socket's remote address, never a client-supplied forwarding header.
/// </summary>
public sealed class NetworkClassifier
{
    private readonly Func<IPAddress, bool> _isLan;
    private readonly IReadOnlyList<IPNetwork> _trusted;
    private readonly bool _loopbackIsLocal;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkClassifier"/> class.
    /// </summary>
    /// <param name="isLan">Jellyfin's LAN test.</param>
    /// <param name="trusted">Extra networks treated like the LAN.</param>
    public NetworkClassifier(Func<IPAddress, bool> isLan, IEnumerable<IPNetwork> trusted)
        : this(isLan, trusted, loopbackIsLocal: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkClassifier"/> class, optionally classifying
    /// loopback like any other address so tests can play an internet client from this machine.
    /// </summary>
    /// <param name="isLan">Jellyfin's LAN test.</param>
    /// <param name="trusted">Extra networks treated like the LAN.</param>
    /// <param name="loopbackIsLocal">Whether loopback is always local.</param>
    internal NetworkClassifier(Func<IPAddress, bool> isLan, IEnumerable<IPNetwork> trusted, bool loopbackIsLocal)
    {
        _isLan = isLan;
        _trusted = trusted.ToList();
        _loopbackIsLocal = loopbackIsLocal;
    }

    /// <summary>
    /// The address a client is counted under for bans and connection limits: IPv4 as is, IPv6 as its /64,
    /// since one home or server usually holds a whole /64 and can pick any address in it.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>The address, or the first address of its /64.</returns>
    public static IPAddress Group(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// The wider IPv6 network an address belongs to, such as its /56 or /48, for limits and bans that catch a
    /// client spreading over many /64s. IPv4 has none, since one address is already what a client holds.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <param name="bits">The prefix length, below 64.</param>
    /// <returns>The first address of the prefix, or null for IPv4.</returns>
    public static IPAddress? Prefix(IPAddress address, int bits)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        var whole = bits / 8;
        if (bits % 8 != 0)
        {
            bytes[whole] &= (byte)(0xFF << (8 - (bits % 8)));
            whole++;
        }

        Array.Clear(bytes, whole, 16 - whole);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// The text a wider IPv6 network is stored and shown as.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <param name="bits">The prefix length, below 64.</param>
    /// <returns>Like <c>2001:db8:1::/48</c>, or null for IPv4.</returns>
    public static string? PrefixKey(IPAddress address, int bits)
        => Prefix(address, bits) is { } prefix ? prefix + "/" + bits.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The keys an address is counted under for bans and limits, narrowest first: its group, then for IPv6 its
    /// /56 and /48, so a client spreading over many /64s is still caught.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>One key for IPv4, three for IPv6.</returns>
    public static IEnumerable<string> TierKeys(IPAddress address)
    {
        yield return GroupKey(address);
        if (PrefixKey(address, 56) is { } site)
        {
            yield return site;
        }

        if (PrefixKey(address, 48) is { } network)
        {
            yield return network;
        }
    }

    /// <summary>
    /// Reads back a stored group: an address, or the first address of a network such as an IPv6 /64.
    /// </summary>
    /// <param name="key">The stored text.</param>
    /// <returns>The address, or null when the text is neither.</returns>
    public static IPAddress? ParseKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (IPAddress.TryParse(key, out var address))
        {
            return address;
        }

        return TryParseNetwork(key, out var network) ? network.BaseAddress : null;
    }

    /// <summary>
    /// The text a client's group is stored and shown as: an IPv4 address, or an IPv6 /64.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>Like <c>203.0.113.7</c> or <c>2001:db8:1:2::/64</c>.</returns>
    public static string GroupKey(IPAddress address)
    {
        var group = Group(address);
        return group.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !group.IsIPv4MappedToIPv6
            ? group + "/64"
            : group.ToString();
    }

    /// <summary>
    /// Whether an address is local. A missing address counts as the internet.
    /// </summary>
    /// <param name="address">The client address.</param>
    /// <returns>True for the LAN, loopback, or a trusted network.</returns>
    public bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var ip = Normalize(address);
        if (_loopbackIsLocal && IPAddress.IsLoopback(ip))
        {
            return true;
        }

        return _trusted.Any(n => n.Contains(ip)) || _isLan(ip);
    }

    /// <summary>
    /// Maps an IPv4-mapped IPv6 address back to IPv4, so dual-stack sockets classify like IPv4 ones.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The normalized address.</returns>
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Parses addresses and CIDR blocks, skipping anything invalid.
    /// </summary>
    /// <param name="values">The raw entries.</param>
    /// <returns>The parsed networks.</returns>
    public static List<IPNetwork> ParseNetworks(IEnumerable<string>? values)
    {
        var result = new List<IPNetwork>();
        foreach (var raw in values ?? Enumerable.Empty<string>())
        {
            if (TryParseNetwork(raw, out var network))
            {
                result.Add(network);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses one address or CIDR block. A bare address becomes a single-host network.
    /// </summary>
    /// <param name="value">The raw entry.</param>
    /// <param name="network">The parsed network.</param>
    /// <returns>True when the entry is valid.</returns>
    public static bool TryParseNetwork(string? value, out IPNetwork network)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Contains('/', StringComparison.Ordinal))
        {
            return IPNetwork.TryParse(text, out network);
        }

        if (IPAddress.TryParse(text, out var ip))
        {
            ip = Normalize(ip);
            network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        network = default;
        return false;
    }
}
