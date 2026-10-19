using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the name the live DNS check resolves for each provider family and the identity comparison
/// that decides when a saved edit starts a record's status over.
/// </summary>
public class RecordTargetTests
{
    private static string? Name(DNSProviderKind kind, string host, string zone = "")
        => RecordTarget.ResolvableName(new DNSRecord { Provider = kind, Hostname = host, Zone = zone });

    [Theory]
    [InlineData(DNSProviderKind.DnsMadeEasy, "123456")]
    [InlineData(DNSProviderKind.DnsMadeEasy, "home.example.com")]
    [InlineData(DNSProviderKind.Sitelutions, "987654")]
    public void RecordIdProviders_HaveNoName(DNSProviderKind kind, string host)
        => Assert.Null(Name(kind, host));

    [Theory]
    [InlineData("myhome", "myhome.duckdns.org")]
    [InlineData("myhome.duckdns.org", "myhome.duckdns.org")]
    [InlineData(" myhome ", "myhome.duckdns.org")]
    [InlineData("one,two", null)]
    [InlineData("home.example.com", null)]
    [InlineData("", null)]
    public void DuckDns(string host, string? expected)
        => Assert.Equal(expected, Name(DNSProviderKind.DuckDns, host));

    [Theory]
    [InlineData(DNSProviderKind.Cloudflare, "home", "example.com", "home.example.com")]
    [InlineData(DNSProviderKind.Cloudflare, "home.example.com", "example.com", "home.example.com")]
    [InlineData(DNSProviderKind.Cloudflare, "@", "example.com", "example.com")]
    [InlineData(DNSProviderKind.Cloudflare, "example.com", "example.com", "example.com")]
    [InlineData(DNSProviderKind.Cloudflare, "home.other.org", "example.com", "home.other.org")]
    [InlineData(DNSProviderKind.Hetzner, "home", "example.com", "home.example.com")]
    [InlineData(DNSProviderKind.Hetzner, "@", "example.com", "example.com")]
    [InlineData(DNSProviderKind.Hetzner, "home.lab", "example.com", "home.lab.example.com")]
    [InlineData(DNSProviderKind.Gandi, "www", "example.com.", "www.example.com")]
    [InlineData(DNSProviderKind.GoDaddy, "home.example.com", "example.com", "home.example.com")]
    [InlineData(DNSProviderKind.Porkbun, "home.example.com", "", "home.example.com")]
    [InlineData(DNSProviderKind.Porkbun, "home", "", null)]
    public void ZoneProviders(DNSProviderKind kind, string host, string zone, string? expected)
        => Assert.Equal(expected, Name(kind, host, zone));

    [Fact]
    public void ZoneGivenAsAnId_CannotCompleteABareLabel()
    {
        Assert.Null(Name(DNSProviderKind.Hetzner, "home", "4f8a9c1b"));
        Assert.Equal("home.example.com", Name(DNSProviderKind.Hetzner, "home.example.com", "4f8a9c1b"));
    }

    [Theory]
    [InlineData("home.example.com", "home.example.com")]
    [InlineData("home.example.com.", "home.example.com")]
    [InlineData("home", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("2001:db8::1", null)]
    [InlineData("1.2.3", null)]
    [InlineData("*.example.com", null)]
    [InlineData("", null)]
    public void FullNameProviders(string host, string? expected)
        => Assert.Equal(expected, Name(DNSProviderKind.DynDns2, host));

    [Fact]
    public void SameTarget_IgnoresNameEnabledAndCase()
    {
        var a = new DNSRecord { Name = "a", Hostname = "Home.Example.com", Login = "enc:1", Password = "enc:2" };
        var b = new DNSRecord { Name = "b", Enabled = false, Hostname = "home.example.com.", Login = "enc:1", Password = "enc:2" };
        Assert.True(RecordTarget.SameTarget(a, b));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("hostname")]
    [InlineData("zone")]
    [InlineData("server")]
    [InlineData("login")]
    [InlineData("password")]
    public void SameTarget_DetectsIdentityAndCredentialChanges(string field)
    {
        var a = new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Server = "", Login = "enc:1", Password = "enc:2" };
        var b = a.Clone();
        switch (field)
        {
            case "provider": b.Provider = DNSProviderKind.Hetzner; break;
            case "hostname": b.Hostname = "other.example.com"; break;
            case "zone": b.Zone = "example.org"; break;
            case "server": b.Server = "api.example.net"; break;
            case "login": b.Login = "enc:3"; break;
            default: b.Password = "enc:4"; break;
        }

        Assert.False(RecordTarget.SameTarget(a, b));
    }
}
