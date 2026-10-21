using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The pure building blocks of the proxy: hostname matching, LAN classification, credential masking and
/// Jellyfin path classification.
/// </summary>
public class ProxyRulesTests
{
    [Theory]
    [InlineData("media.example.com", "media.example.com", true)]
    [InlineData("MEDIA.example.com.", "media.example.com", true)]
    [InlineData("other.example.com", "media.example.com", false)]
    [InlineData("media.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("a.b.example.com", "*.example.com", false)]
    [InlineData("evilexample.com", "*.example.com", false)]
    [InlineData("", "media.example.com", false)]
    public void Hostname_MatchesLikeCertificates(string host, string pattern, bool expected)
        => Assert.Equal(expected, HostnameMatcher.Matches(host, pattern));

    [Theory]
    [InlineData(" Media.Example.COM. ", "media.example.com")]
    [InlineData("*.example.com", "*.example.com")]
    [InlineData("*.com", "")]
    [InlineData("localhost", "")]
    [InlineData("https://media.example.com", "")]
    [InlineData("media.example.com:8920", "")]
    [InlineData("-bad.example.com", "")]
    [InlineData("a.*.example.com", "")]
    public void Hostname_NormalizesOrRejects(string raw, string expected)
        => Assert.Equal(expected, HostnameMatcher.Normalize(raw));

    [Fact]
    public void Classifier_UsesLanTestTrustedNetworksAndLoopback()
    {
        var lan = IPNetwork.Parse("192.168.1.0/24");
        var classifier = new NetworkClassifier(ip => lan.Contains(ip), NetworkClassifier.ParseNetworks(new[] { "100.64.0.0/10", "203.0.113.9", "junk" }));

        Assert.True(classifier.IsLocal(IPAddress.Parse("192.168.1.20")));
        Assert.True(classifier.IsLocal(IPAddress.Parse("::ffff:192.168.1.20")));
        Assert.True(classifier.IsLocal(IPAddress.Parse("100.100.1.1")));
        Assert.True(classifier.IsLocal(IPAddress.Parse("203.0.113.9")));
        Assert.True(classifier.IsLocal(IPAddress.Loopback));
        Assert.False(classifier.IsLocal(IPAddress.Parse("203.0.113.10")));
        Assert.False(classifier.IsLocal(null));
    }

    [Theory]
    [InlineData("/Videos/1/stream?api_key=abc123&static=true", "/Videos/1/stream?api_key=***&static=true")]
    [InlineData("/Items/1/Images/Primary?ApiKey=SECRET", "/Items/1/Images/Primary?ApiKey=***")]
    [InlineData("GET http://host/x?token=t0k&access_token=a1 failed", "GET http://host/x?token=***&access_token=*** failed")]
    [InlineData("MediaBrowser Client=\"web\", Token=\"deadbeef\"", "MediaBrowser Client=\"web\", Token=\"***\"")]
    [InlineData("/Users/Public?tokenize=1", "/Users/Public?tokenize=1")]
    public void Redactor_MasksEveryTokenForm(string input, string expected)
        => Assert.Equal(expected, CredentialRedactor.Redact(input));

    [Theory]
    [InlineData("/Startup/Configuration", true)]
    [InlineData("/startup/user", true)]
    [InlineData("/StartupThing", false)]
    [InlineData("/System/Info", false)]
    public void Path_Startup(string path, bool expected) => Assert.Equal(expected, PathRules.IsStartup(path));

    [Theory]
    [InlineData("/Videos/1/stream.mkv", true)]
    [InlineData("/videos/1/hls1/main/0.ts", true)]
    [InlineData("/Audio/1/universal", true)]
    [InlineData("/Items/1/Download", true)]
    [InlineData("/LiveTv/LiveRecordings/1/stream", true)]
    [InlineData("/LiveTv/Channels", false)]
    [InlineData("/Items/1/Images/Primary", false)]
    [InlineData("/Items", false)]
    [InlineData("/Users/1/Items", false)]
    public void Path_Media(string path, bool expected) => Assert.Equal(expected, PathRules.IsMedia(path));

    [Theory]
    [InlineData("/Users/AuthenticateByName", true)]
    [InlineData("/users/authenticatebyname/", true)]
    [InlineData("/Users/AuthenticateByName//", true)]
    [InlineData("/Users/AuthenticateWithQuickConnect/", true)]
    [InlineData("/Users/0123456789abcdef/Authenticate", true)]
    [InlineData("/Users/0123456789abcdef/Authenticate/", true)]
    [InlineData("/Users/Public", false)]
    [InlineData("/Users/0123/Images/Primary", false)]
    public void Path_SignIn(string path, bool expected) => Assert.Equal(expected, PathRules.IsSignIn(path));

    [Theory]
    [InlineData("media.example.com", true)]
    [InlineData("*.example.co.uk", true)]
    [InlineData("nas.lan", false)]
    [InlineData("media.home.arpa", false)]
    [InlineData("server.local", false)]
    [InlineData("localhost", false)]
    [InlineData("203.0.113.7", false)]
    public void Hostname_IsPublicName(string name, bool expected) => Assert.Equal(expected, HostnameMatcher.IsPublicName(name));

    [Fact]
    public void Group_PutsIPv6InItsSlash64()
    {
        Assert.Equal("2001:db8:1:2::/64", NetworkClassifier.GroupKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.Equal(NetworkClassifier.GroupKey(IPAddress.Parse("2001:db8:1:2::1")), NetworkClassifier.GroupKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.Equal("203.0.113.7", NetworkClassifier.GroupKey(IPAddress.Parse("203.0.113.7")));
    }

    [Fact]
    public void Path_StripsBaseUrl()
    {
        Assert.Equal("/Startup/User", PathRules.StripBase("/jellyfin/Startup/User", "/jellyfin").Value);
        Assert.Equal("/Startup/User", PathRules.StripBase("/Startup/User", string.Empty).Value);
        Assert.Equal("/other/Startup", PathRules.StripBase("/other/Startup", "/jellyfin").Value);
    }
}
