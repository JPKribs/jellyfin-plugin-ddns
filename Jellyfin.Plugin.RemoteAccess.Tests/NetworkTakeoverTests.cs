using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using MediaBrowser.Common.Net;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The pure parts of trusting the proxy: what is written to Jellyfin, and that restoring puts back exactly
/// what was captured.
/// </summary>
public class NetworkTakeoverTests
{
    private static NetworkConfiguration Original() => new()
    {
        InternalHttpPort = 8096,
        InternalHttpsPort = 8920,
        EnableHttps = true,
        RequireHttps = true,
        EnableRemoteAccess = false,
        PublicHttpPort = 8096,
        PublicHttpsPort = 8920,
        LocalNetworkAddresses = new[] { "192.168.1.10" },
        KnownProxies = new[] { "10.0.0.5" },
        PublishedServerUriBySubnet = new[] { "all=https://old.example.com" }
    };

    private static ProxySettings Settings() => new()
    {
        Enabled = true,
        Hostnames = new List<string> { "*.example.com", "media.example.com" }
    };

    private static ProxySettings LoopbackOnly()
    {
        var settings = Settings();
        settings.Advanced.JellyfinLoopbackOnly = true;
        return settings;
    }

    [Fact]
    public void OnlyThroughTheProxy_BindsToThisMachine_AndUndoPutsItBack()
    {
        var network = Original();
        network.EnableIPv4 = true;
        network.EnableIPv6 = true;
        network.PublishedServerUriBySubnet = new[] { "internal=http://192.168.1.10:8096" };
        var snapshot = NetworkTakeover.Capture(network);
        snapshot.BoundToLoopback = true;
        snapshot.WrittenLocal = NetworkTakeover.LocalEntries(LoopbackOnly()).ToList();
        NetworkTakeover.ApplyManaged(network, LoopbackOnly(), snapshot);

        // Nothing but this machine can reach Jellyfin, and local apps are sent to the public address.
        Assert.Equal(new[] { "127.0.0.1", "::1" }, network.LocalNetworkAddresses);
        // Jellyfin tells LAN clients apart by the networks it listens on, only this machine's now, so every
        // address range is sent to the public address rather than "internal".
        Assert.Contains("0.0.0.0/0=https://media.example.com", network.PublishedServerUriBySubnet);
        Assert.Contains("::/0=https://media.example.com", network.PublishedServerUriBySubnet);
        Assert.DoesNotContain("internal=http://192.168.1.10:8096", network.PublishedServerUriBySubnet);
        Assert.Equal(new Uri("http://127.0.0.1:8096/"), NetworkTakeover.BackendUri(network));

        NetworkTakeover.Restore(network, snapshot);
        Assert.Equal(new[] { "192.168.1.10" }, network.LocalNetworkAddresses);
        Assert.Equal(new[] { "internal=http://192.168.1.10:8096" }, network.PublishedServerUriBySubnet);
    }

    [Fact]
    public void OnlyThroughTheProxy_TurnedOffWhileTrusted_PutsTheAddressesBack()
    {
        var network = Original();
        network.EnableIPv4 = true;
        network.EnableIPv6 = false;
        var snapshot = NetworkTakeover.Capture(network);
        var written = NetworkTakeover.LocalEntries(LoopbackOnly());
        NetworkTakeover.ApplyManaged(network, LoopbackOnly(), snapshot);
        Assert.Equal(new[] { "127.0.0.1" }, network.LocalNetworkAddresses);

        // Applied again with the option off: the original bind comes back, with loopback added as usual, in
        // IPv4 alone since IPv6 is off.
        NetworkTakeover.ApplyManaged(network, Settings(), snapshot, wasBound: true, writtenLocal: written);
        Assert.Equal(new[] { "192.168.1.10", "127.0.0.1" }, network.LocalNetworkAddresses);
        Assert.All(written, w => Assert.DoesNotContain(w, network.PublishedServerUriBySubnet));
        Assert.Equal(new[] { "all=https://old.example.com", "external=https://media.example.com" }, network.PublishedServerUriBySubnet);
    }

    [Fact]
    public void OnlyThroughTheProxy_AnOlderVersionsInternalEntry_IsStillRemovedOnUndo()
    {
        var network = Original();
        var snapshot = NetworkTakeover.Capture(network);
        snapshot.BoundToLoopback = true;
        snapshot.WrittenInternal = "internal=https://media.example.com";
        network.LocalNetworkAddresses = new[] { "127.0.0.1" };
        network.PublishedServerUriBySubnet = new[] { "internal=https://media.example.com", "external=https://media.example.com" };
        snapshot.WrittenExternal = "external=https://media.example.com";

        NetworkTakeover.Restore(network, snapshot);
        Assert.Equal(new[] { "all=https://old.example.com" }, network.PublishedServerUriBySubnet);
        Assert.Equal(new[] { "192.168.1.10" }, network.LocalNetworkAddresses);
    }

    [Fact]
    public void Backend_WithIPv4Off_IsReachedOverIPv6()
    {
        var network = new NetworkConfiguration { EnableIPv4 = false, EnableIPv6 = true, InternalHttpPort = 8096 };
        Assert.Equal(new Uri("http://[::1]:8096/"), NetworkTakeover.BackendUri(network));
    }

    [Fact]
    public void OnlyThroughTheProxy_NeedsAnExactAddress()
    {
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { "*.example.com" } };
        settings.Advanced.JellyfinLoopbackOnly = true;
        ProxySettingsValidator.Normalize(settings);
        Assert.Contains(ProxySettingsValidator.Validate(settings), e => e.Contains("Only through the proxy", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyManaged_TrustsTheProxyAndLeavesThePortsAlone()
    {
        var network = Original();
        NetworkTakeover.ApplyManaged(network, Settings());

        // Jellyfin keeps its ports and its own HTTPS.
        var original = Original();
        Assert.Equal(original.InternalHttpPort, network.InternalHttpPort);
        Assert.Equal(original.InternalHttpsPort, network.InternalHttpsPort);
        Assert.Equal(original.EnableHttps, network.EnableHttps);
        Assert.Equal(original.RequireHttps, network.RequireHttps);
        Assert.Equal(original.PublicHttpPort, network.PublicHttpPort);
        Assert.Equal(original.PublicHttpsPort, network.PublicHttpsPort);

        Assert.True(network.EnableRemoteAccess);
        Assert.Equal(new[] { "10.0.0.5", "127.0.0.1", "::1" }, network.KnownProxies);

        // Bound to a LAN address: loopback is added so the proxy reaches Jellyfin from a trusted address.
        // Loopback only in the families Jellyfin uses: IPv6 is off by default, and Jellyfin will not bind ::1 then.
        Assert.Equal(new[] { "192.168.1.10", "127.0.0.1" }, network.LocalNetworkAddresses);
        Assert.Equal(new Uri("http://127.0.0.1:8096/"), NetworkTakeover.BackendUri(network));
        Assert.True(NetworkTakeover.TrustsForwarding(network));

        // The administrator's own published entries stay; the internet's points at the public name.
        Assert.Equal(new[] { "all=https://old.example.com", "external=https://media.example.com" }, network.PublishedServerUriBySubnet);
    }

    [Fact]
    public void ApplyManaged_AllInterfacesStayAllInterfaces()
    {
        var network = Original();
        network.LocalNetworkAddresses = Array.Empty<string>();
        NetworkTakeover.ApplyManaged(network, Settings());
        Assert.Empty(network.LocalNetworkAddresses);
    }

    [Fact]
    public void Restore_PutsBackExactlyWhatWasCaptured()
    {
        var network = Original();
        var snapshot = NetworkTakeover.Capture(network);
        NetworkTakeover.ApplyManaged(network, Settings());

        // A setting the takeover never touches, changed while managed, survives the restore.
        network.BaseUrl = "/jf";
        NetworkTakeover.Restore(network, snapshot);

        var original = Original();
        Assert.Equal(original.EnableRemoteAccess, network.EnableRemoteAccess);
        Assert.Equal(original.LocalNetworkAddresses, network.LocalNetworkAddresses);
        Assert.Equal(original.KnownProxies, network.KnownProxies);
        Assert.Equal(original.PublishedServerUriBySubnet, network.PublishedServerUriBySubnet);
        Assert.Equal("/jf", network.BaseUrl);
    }

    [Fact]
    public void Restore_KeepsWhatTheAdministratorChangedWhileTrusted()
    {
        var network = Original();
        var snapshot = NetworkTakeover.Capture(network);
        NetworkTakeover.ApplyManaged(network, Settings());

        // Changed while trusted: a new port, HTTPS off, another known proxy and another published entry.
        network.InternalHttpPort = 8097;
        network.EnableHttps = false;
        network.KnownProxies = network.KnownProxies.Append("10.0.0.9").ToArray();
        network.PublishedServerUriBySubnet = network.PublishedServerUriBySubnet.Append("internal=http://192.168.1.10:8097").ToArray();
        NetworkTakeover.Restore(network, snapshot);

        Assert.Equal(8097, network.InternalHttpPort);
        Assert.False(network.EnableHttps);
        Assert.False(network.EnableRemoteAccess);
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.9" }, network.KnownProxies);
        Assert.Equal(new[] { "192.168.1.10" }, network.LocalNetworkAddresses);
        Assert.Equal(new[] { "all=https://old.example.com", "internal=http://192.168.1.10:8097" }, network.PublishedServerUriBySubnet);
    }

    [Fact]
    public void Restore_KeepsLoopbackThatWasListedBefore()
    {
        var network = Original();
        network.KnownProxies = new[] { "127.0.0.1" };
        network.PublishedServerUriBySubnet = new[] { "external=https://old.example.com" };
        var snapshot = NetworkTakeover.Capture(network);
        NetworkTakeover.ApplyManaged(network, Settings());
        NetworkTakeover.Restore(network, snapshot);

        Assert.Equal(new[] { "127.0.0.1" }, network.KnownProxies);
        Assert.Equal(new[] { "external=https://old.example.com" }, network.PublishedServerUriBySubnet);
    }

    [Fact]
    public void PublishedUris_ReplaceOnlyTheExternalEntry()
    {
        var uris = NetworkTakeover.PublishedUris(new[] { "internal=http://192.168.1.10:8096", "external=https://old.example.com" }, Settings()).ToList();
        Assert.Equal(new[] { "internal=http://192.168.1.10:8096", "external=https://media.example.com" }, uris);

        // Without a public name nothing is replaced.
        var none = new ProxySettings { Hostnames = new List<string> { "*.example.com" } };
        Assert.Equal(new[] { "external=https://old.example.com" }, NetworkTakeover.PublishedUris(new[] { "external=https://old.example.com" }, none));
    }

    [Theory]
    [InlineData(new string[0], "http://127.0.0.1:8096/")]
    [InlineData(new[] { "127.0.0.1", "::1" }, "http://127.0.0.1:8096/")]
    [InlineData(new[] { "0.0.0.0" }, "http://127.0.0.1:8096/")]
    [InlineData(new[] { "192.168.1.10" }, "http://192.168.1.10:8096/")]
    [InlineData(new[] { "eth0" }, "http://127.0.0.1:8096/")]
    public void BackendUri_FollowsBindAddresses(string[] bound, string expected)
    {
        var network = new NetworkConfiguration { InternalHttpPort = 8096, LocalNetworkAddresses = bound };
        Assert.Equal(new Uri(expected), NetworkTakeover.BackendUri(network));
    }

    [Fact]
    public void TrustsForwarding_NeedsKnownProxies()
    {
        Assert.False(NetworkTakeover.TrustsForwarding(new NetworkConfiguration { KnownProxies = Array.Empty<string>() }));
        Assert.True(NetworkTakeover.TrustsForwarding(new NetworkConfiguration { KnownProxies = new[] { "127.0.0.1" } }));

        // Bound to a LAN address only: the proxy connects from it, so it must be listed.
        var lanOnly = new NetworkConfiguration { InternalHttpPort = 8096, LocalNetworkAddresses = new[] { "192.168.1.10" }, KnownProxies = new[] { "127.0.0.1" } };
        Assert.False(NetworkTakeover.TrustsForwarding(lanOnly));
        lanOnly.KnownProxies = new[] { "127.0.0.1", "192.168.1.10" };
        Assert.True(NetworkTakeover.TrustsForwarding(lanOnly));
    }

    [Fact]
    public void NetworkView_UsesRunningPort()
    {
        var view = JellyfinNetworkView.From(new NetworkConfiguration { InternalHttpPort = 9999, BaseUrl = "/jf" }, 8096, 0);
        Assert.Equal(new Uri("http://127.0.0.1:8096/"), view.Backend);
        Assert.Equal("/jf", view.BaseUrl);
    }
}
