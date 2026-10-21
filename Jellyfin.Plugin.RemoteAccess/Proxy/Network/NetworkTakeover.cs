using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// The pure parts of trusting the proxy: what to capture, what to write, and what to put back. Jellyfin keeps its
/// own ports and HTTPS; it only learns to trust the proxy on this machine and to accept remote clients.
/// </summary>
public static class NetworkTakeover
{
    private static readonly string[] _loopback = { "127.0.0.1", "::1" };

    /// <summary>
    /// Captures the settings trusting the proxy changes.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration.</param>
    /// <returns>The snapshot.</returns>
    public static NetworkSnapshot Capture(NetworkConfiguration network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return new NetworkSnapshot
        {
            EnableRemoteAccess = network.EnableRemoteAccess,
            LocalNetworkAddresses = network.LocalNetworkAddresses.ToList(),
            KnownProxies = network.KnownProxies.ToList(),
            PublishedServerUriBySubnet = network.PublishedServerUriBySubnet.ToList()
        };
    }

    /// <summary>
    /// Writes the managed values: Jellyfin trusts the proxy's forwarding headers, accepts remote clients, and
    /// gives internet clients the public HTTPS address. Its ports and HTTPS are left alone. With
    /// <see cref="ProxyOptions.JellyfinLoopbackOnly"/>, Jellyfin also listens on this machine only, and local
    /// clients are given the public address too, since its local one can no longer be reached.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration, changed in place.</param>
    /// <param name="settings">The proxy settings.</param>
    /// <param name="snapshot">The settings from before trusting, for putting back what an earlier apply replaced.</param>
    /// <param name="wasBound">Whether an earlier apply bound Jellyfin to this machine only.</param>
    /// <param name="writtenLocal">The URL entries for every client that earlier apply wrote.</param>
    public static void ApplyManaged(NetworkConfiguration network, ProxySettings settings, NetworkSnapshot? snapshot = null, bool wasBound = false, IEnumerable<string>? writtenLocal = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(settings);
        network.EnableRemoteAccess = true;
        network.KnownProxies = network.KnownProxies
            .Concat(_loopback)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var loopbackOnly = ProxyOptions.From(settings.Advanced).JellyfinLoopbackOnly;
        if (loopbackOnly)
        {
            network.LocalNetworkAddresses = LoopbackBinds(network);
            network.PublishedServerUriBySubnet = PublishedUris(network.PublishedServerUriBySubnet, settings, forLocalToo: true).ToArray();
            return;
        }

        // The option was turned off while trusting: what binding to this machine replaced comes back first.
        if (wasBound && snapshot is not null)
        {
            if (IsLoopbackOnly(network.LocalNetworkAddresses))
            {
                network.LocalNetworkAddresses = snapshot.LocalNetworkAddresses.ToArray();
            }

            network.PublishedServerUriBySubnet = PutBackLocal(network.PublishedServerUriBySubnet, snapshot, writtenLocal);
        }

        if (network.LocalNetworkAddresses.Any(a => !string.IsNullOrWhiteSpace(a)))
        {
            // Bound to specific addresses: add loopback, so the proxy reaches Jellyfin from an address
            // Jellyfin trusts as a proxy rather than from a LAN address it would treat as a local client. Only in
            // the families Jellyfin uses: it refuses to bind an address in one that is off.
            network.LocalNetworkAddresses = network.LocalNetworkAddresses
                .Concat(LoopbackBinds(network))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        network.PublishedServerUriBySubnet = PublishedUris(network.PublishedServerUriBySubnet, settings).ToArray();
    }

    /// <summary>
    /// Undoes <see cref="ApplyManaged"/>: remote access goes back to how it was before, and only the loopback
    /// entries and the public URL it added are removed. Anything else changed since, such as ports, HTTPS,
    /// another known proxy or a public URL the administrator set, is kept.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration, changed in place.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void Restore(NetworkConfiguration network, NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(snapshot);
        network.EnableRemoteAccess = snapshot.EnableRemoteAccess;
        network.KnownProxies = WithoutAdded(network.KnownProxies, snapshot.KnownProxies, _loopback);
        network.LocalNetworkAddresses = snapshot.BoundToLoopback && IsLoopbackOnly(network.LocalNetworkAddresses)
            ? snapshot.LocalNetworkAddresses.ToArray()
            : WithoutAdded(network.LocalNetworkAddresses, snapshot.LocalNetworkAddresses, _loopback);
        network.PublishedServerUriBySubnet = PutBackLocal(network.PublishedServerUriBySubnet, snapshot, snapshot.AllWrittenLocal);

        // The external entry trusting wrote is removed and any the administrator had before comes back. When the
        // entry is no longer the one written, the administrator changed it since, and theirs stays.
        var entries = network.PublishedServerUriBySubnet.ToList();
        var written = snapshot.WrittenExternal;
        if (written is null)
        {
            entries = entries
                .Where(e => !IsExternal(e) || snapshot.PublishedServerUriBySubnet.Contains(e, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        else if (written.Length > 0 && entries.RemoveAll(e => string.Equals(e, written, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            network.PublishedServerUriBySubnet = entries.ToArray();
            return;
        }

        entries.AddRange(snapshot.PublishedServerUriBySubnet.Where(e => IsExternal(e) && !entries.Contains(e, StringComparer.OrdinalIgnoreCase)));
        network.PublishedServerUriBySubnet = entries.ToArray();
    }

    /// <summary>
    /// The public URL entry <see cref="ApplyManaged"/> writes for these settings.
    /// </summary>
    /// <param name="settings">The proxy settings.</param>
    /// <returns>The entry, or empty when no exact hostname is set.</returns>
    public static string ExternalEntry(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var publicName = settings.Hostnames.FirstOrDefault(h => !h.StartsWith('*'));
        return publicName is null ? string.Empty : "external=https://" + publicName;
    }

    // The current entries without the ones trusting added, keeping any that were there before it.
    private static string[] WithoutAdded(IEnumerable<string> current, ICollection<string> before, IEnumerable<string> added)
        => current
            .Where(e => !added.Contains(e, StringComparer.OrdinalIgnoreCase) || before.Contains(e, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// The URL entries written while Jellyfin listens on this machine only: every client, by address range, is
    /// given the public address. Jellyfin works out which clients are internal from the networks it listens on,
    /// which are then this machine's alone, so an <c>internal</c> entry would miss the LAN.
    /// </summary>
    /// <param name="settings">The proxy settings.</param>
    /// <returns>The entries, or none when no exact hostname is set.</returns>
    public static IReadOnlyList<string> LocalEntries(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var publicName = settings.Hostnames.FirstOrDefault(h => !h.StartsWith('*'));
        return publicName is null
            ? Array.Empty<string>()
            : new[] { "0.0.0.0/0=https://" + publicName, "::/0=https://" + publicName };
    }

    /// <summary>
    /// Whether every bind address is loopback, as binding to this machine only leaves them.
    /// </summary>
    /// <param name="addresses">Jellyfin's bind addresses.</param>
    /// <returns>True when there is at least one and all are loopback.</returns>
    public static bool IsLoopbackOnly(IEnumerable<string> addresses)
    {
        var list = (addresses ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        return list.Count > 0 && list.All(a => IPAddress.TryParse(a, out var ip) && IPAddress.IsLoopback(ip));
    }

    // Loopback in each address family Jellyfin uses, so it binds nothing another machine can reach.
    private static string[] LoopbackBinds(NetworkConfiguration network)
    {
        var binds = new List<string>(2);
        if (network.EnableIPv4)
        {
            binds.Add("127.0.0.1");
        }

        if (network.EnableIPv6)
        {
            binds.Add("::1");
        }

        return binds.Count > 0 ? binds.ToArray() : new[] { "127.0.0.1" };
    }

    // Removes the entries for every client an apply wrote and brings back the administrator's own from before
    // it. Entries the administrator has changed since stay.
    private static string[] PutBackLocal(IEnumerable<string> current, NetworkSnapshot snapshot, IEnumerable<string>? written)
    {
        var entries = current.ToList();
        var mine = (written ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrEmpty(w)).ToList();
        if (entries.RemoveAll(e => mine.Contains(e, StringComparer.OrdinalIgnoreCase)) == 0)
        {
            return entries.ToArray();
        }

        entries.AddRange(snapshot.PublishedServerUriBySubnet.Where(e => !IsExternal(e) && !entries.Contains(e, StringComparer.OrdinalIgnoreCase)));
        return entries.ToArray();
    }

    private static bool IsExternal(string entry) => entry.StartsWith("external=", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// The published server URLs: the administrator's own entries, with the internet's entry pointing at the
    /// public HTTPS name. Local clients keep reaching Jellyfin directly. A wildcard hostname cannot be
    /// advertised, so the first exact one is used.
    /// </summary>
    /// <param name="existing">The entries already configured.</param>
    /// <param name="settings">The proxy settings.</param>
    /// <param name="forLocalToo">Whether local clients are given the public address too.</param>
    /// <returns>The entries, in Jellyfin's <c>identifier=url</c> form.</returns>
    public static IEnumerable<string> PublishedUris(IEnumerable<string>? existing, ProxySettings settings, bool forLocalToo = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var publicName = settings.Hostnames.FirstOrDefault(h => !h.StartsWith('*'));
        // For every client, the administrator's own entries all go: each would send someone to an address that
        // no longer answers. Undo brings them back.
        foreach (var entry in (existing ?? Enumerable.Empty<string>()).Where(e => publicName is null || !(IsExternal(e) || forLocalToo)))
        {
            yield return entry;
        }

        if (forLocalToo)
        {
            foreach (var entry in LocalEntries(settings))
            {
                yield return entry;
            }
        }

        if (publicName is not null)
        {
            yield return "external=https://" + publicName;
        }
    }

    /// <summary>
    /// Jellyfin's address on this machine, from its network configuration: loopback when it listens there
    /// (which it does unless bound to specific addresses), otherwise its first bound address.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration.</param>
    /// <returns>The backend URL.</returns>
    public static Uri BackendUri(NetworkConfiguration network)
    {
        ArgumentNullException.ThrowIfNull(network);
        // With IPv4 off Jellyfin listens on IPv6 alone.
        var host = network.EnableIPv4 || !network.EnableIPv6 ? "127.0.0.1" : "[::1]";
        var bound = network.LocalNetworkAddresses
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Cast<IPAddress>()
            .ToList();
        if (bound.Count > 0 && !bound.Any(ip => ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
        {
            var first = bound[0];
            host = IPAddress.IPv6Loopback.Equals(first) || first.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + first + "]" : first.ToString();
        }

        return new Uri("http://" + host + ":" + network.InternalHttpPort.ToString(CultureInfo.InvariantCulture) + "/");
    }

    /// <summary>
    /// Whether Jellyfin will honour the proxy's forwarding headers. Jellyfin ignores them entirely while its
    /// known-proxy list is empty, and then every proxied client looks like it is on this machine.
    /// </summary>
    /// <param name="network">Jellyfin's network configuration.</param>
    /// <returns>True when forwarding headers are honoured.</returns>
    public static bool TrustsForwarding(NetworkConfiguration network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var proxies = network.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (proxies.Count == 0)
        {
            return false;
        }

        // ASP.NET trusts loopback once any proxy is listed. A backend reached on another address only trusts
        // the proxy if that address is listed itself.
        var backend = IPAddress.Parse(BackendUri(network).Host.Trim('[', ']'));
        return IPAddress.IsLoopback(backend) || proxies.Any(p => IPAddress.TryParse(p, out var ip) && ip.Equals(backend));
    }

    /// <summary>
    /// Finds this machine's LAN address: the first private IPv4 address on an interface that is up and
    /// has a gateway.
    /// </summary>
    /// <returns>The address, or null when none is found.</returns>
    public static IPAddress? DetectLanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A public address on this machine, such as a global IPv6 address or a VPS's own address, where anything
    /// listening on all interfaces can be reached from the internet without a router forwarding to it.
    /// </summary>
    /// <returns>The address, or null when the machine has none.</returns>
    public static IPAddress? DetectPublicAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => !a.IsIPv6LinkLocal && Models.IPAddressClassifier.IsPublic(a));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
