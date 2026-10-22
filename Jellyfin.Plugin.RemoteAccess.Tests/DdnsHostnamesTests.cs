using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The DDNS hostnames the pages save as the proxy's addresses.
/// </summary>
public class DdnsHostnamesTests
{
    [Fact]
    public void From_BuildsFullNamesPerProvider()
    {
        var records = new[]
        {
            new DNSRecord { Name = "Home", Provider = DNSProviderKind.Cloudflare, Hostname = "Media.Example.com", Enabled = true },
            new DNSRecord { Provider = DNSProviderKind.DuckDns, Hostname = "mybox", Enabled = true },
            new DNSRecord { Name = "Zone", Provider = DNSProviderKind.Hetzner, Hostname = "jf", Zone = "example.net", Enabled = false },
            new DNSRecord { Provider = DNSProviderKind.DnsMadeEasy, Hostname = "12345", Enabled = true },
        };

        var names = DdnsHostnames.From(records);

        Assert.Equal(new[] { "media.example.com", "mybox.duckdns.org", "jf.example.net" }, names.Select(n => n.Hostname));
        Assert.Equal("Home", names[0].Record);
        Assert.Equal("mybox.duckdns.org", names[1].Record);
        Assert.False(names[2].Enabled);
    }

    [Fact]
    public void From_AddsWildcardAndDropsDuplicates()
    {
        var records = new[]
        {
            new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "example.com", Wildcard = true, Enabled = true },
            new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "example.com", Enabled = true },
        };

        var names = DdnsHostnames.From(records);

        // The plain name comes before its wildcard.
        Assert.Equal(new[] { "example.com", "*.example.com" }, names.Select(n => n.Hostname));
    }

    [Fact]
    public void From_NamesTheRecordEachNameComesFrom()
    {
        var record = new DNSRecord { Id = "abc", Provider = DNSProviderKind.Cloudflare, Hostname = "example.com", Wildcard = true, Enabled = true };

        var names = DdnsHostnames.From(new[] { record });

        Assert.All(names, n => Assert.Equal("abc", n.RecordId));
    }
}
