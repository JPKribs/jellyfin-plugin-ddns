using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The redirect address built for plain HTTP sent to the HTTPS port.
/// </summary>
public class PlainHttpRedirectTests
{
    [Theory]
    [InlineData("GET /web/index.html?x=1 HTTP/1.1\r\nHost: localhost:8920\r\nAccept: */*", 8920, "https://localhost:8920/web/index.html?x=1")]
    [InlineData("GET / HTTP/1.1\r\nhost: Media.Example.com", 443, "https://Media.Example.com/")]
    [InlineData("GET /a HTTP/1.1\r\nHost: [::1]:8920", 8920, "https://[::1]:8920/a")]
    [InlineData("GET /a HTTP/1.1\r\nHost: 192.168.1.10:8920", 8920, "https://192.168.1.10:8920/a")]
    [InlineData("PRI * HTTP/2.0", 8920, "https://192.168.1.5:8920/")]
    [InlineData("GET http://elsewhere.example/ HTTP/1.1\r\nHost: localhost", 8920, "https://localhost:8920/")]
    [InlineData("", 8920, "https://192.168.1.5:8920/")]
    public void BuildsTheSameAddressOverHttps(string head, int port, string expected)
        => Assert.Equal(expected, PlainHttpRedirect.RedirectLocation(head, IPAddress.Parse("192.168.1.5"), port));

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: evil.example/\"><script>")]
    [InlineData("GET / HTTP/1.1\r\nHost: a b")]
    [InlineData("GET / HTTP/1.1\r\nHost: [not-an-ip]")]
    [InlineData("GET / HTTP/1.1\r\nHost: [fe80::1%eth0]")]
    public void UnusableHost_FallsBackToTheServerAddress(string head)
        => Assert.Equal("https://192.168.1.5:8920/", PlainHttpRedirect.RedirectLocation(head, IPAddress.Parse("192.168.1.5"), 8920));

    [Fact]
    public void UnsafeTarget_FallsBackToRoot()
        => Assert.Equal("https://localhost:8920/", PlainHttpRedirect.RedirectLocation("GET /a\u0001b HTTP/1.1\r\nHost: localhost", null, 8920));

    [Fact]
    public void AnyAddressFallback_UsesLoopback()
        => Assert.Equal("https://127.0.0.1:8920/", PlainHttpRedirect.RedirectLocation(string.Empty, IPAddress.Any, 8920));
}
