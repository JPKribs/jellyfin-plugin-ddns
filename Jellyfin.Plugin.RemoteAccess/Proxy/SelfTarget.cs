using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Recognises a Service target that is this machine on Jellyfin's or the proxy's own port. Through such a
/// route Jellyfin would skip every internet rule meant for it, and the proxy would forward to itself until
/// every connection is used.
/// </summary>
public static class SelfTarget
{
    /// <summary>
    /// Whether a target is this machine on one of the given ports.
    /// </summary>
    /// <param name="target">The target address.</param>
    /// <param name="ports">Jellyfin's and the proxy's ports; zero entries are ignored.</param>
    /// <returns>True when the target is reserved.</returns>
    public static bool IsReserved(Uri target, IEnumerable<int> ports)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ports);
        return ports.Where(p => p > 0).Contains(target.Port) && IsThisMachine(target.Host);
    }

    /// <summary>
    /// Whether a host names this machine: loopback, localhost, this machine's name, or one of its addresses.
    /// </summary>
    /// <param name="host">The host, as in a URL.</param>
    /// <returns>True for this machine.</returns>
    public static bool IsThisMachine(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(name, out var ip))
        {
            return false;
        }

        return IsThisMachine(ip);
    }

    /// <summary>
    /// Whether an address, such as one a Service's name resolved to, is this machine.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>True for loopback, the unspecified address, or one of this machine's addresses.</returns>
    public static bool IsThisMachine(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = NetworkClassifier.Normalize(address);
        return IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.Any)
            || ip.Equals(IPAddress.IPv6Any)
            || LocalAddresses().Any(a => NetworkClassifier.Normalize(a).Equals(ip));
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
