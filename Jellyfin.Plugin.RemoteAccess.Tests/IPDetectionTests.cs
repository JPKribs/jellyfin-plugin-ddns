using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the public-address filter in <see cref="IPDetectionService"/>: a detection endpoint that
/// returns a private, reserved, or wrong-family address must be rejected (null), and a real public
/// address must pass through. Exercised through the public detection path with a stubbed endpoint.
/// </summary>
public class IPDetectionTests
{
    private static async Task<string?> DetectV4(string endpointReply)
    {
        var svc = new IPDetectionService(StubHttp.Always(HttpStatusCode.OK, endpointReply), NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://probe", IPv6DetectionUrl = string.Empty };
        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);
        return ip.IPv4;
    }

    private static async Task<string?> DetectV6(string endpointReply)
    {
        var svc = new IPDetectionService(StubHttp.Always(HttpStatusCode.OK, endpointReply), NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = string.Empty, IPv6DetectionUrl = "https://probe" };
        var ip = await svc.DetectAsync(cfg, needIPv4: false, needIPv6: true, CancellationToken.None);
        return ip.IPv6;
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.114.5")]      // just outside TEST-NET-3 (203.0.113.0/24)
    [InlineData("100.63.255.255")]   // just below CGNAT (100.64/10)
    [InlineData("100.128.0.1")]      // just above CGNAT
    public async Task PublicV4_Accepted(string ip)
        => Assert.Equal(ip, await DetectV4(ip));

    [Theory]
    [InlineData("0.0.0.0")]          // "this network"
    [InlineData("10.0.0.1")]         // private
    [InlineData("172.16.5.5")]       // private
    [InlineData("192.168.1.1")]      // private
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("169.254.1.1")]      // link-local
    [InlineData("192.0.2.1")]        // TEST-NET-1
    [InlineData("198.18.0.1")]       // benchmarking
    [InlineData("198.51.100.1")]     // TEST-NET-2
    [InlineData("203.0.113.1")]      // TEST-NET-3
    [InlineData("224.0.0.1")]        // multicast
    public async Task PrivateOrReservedV4_Rejected(string ip)
        => Assert.Null(await DetectV4(ip));

    [Theory]
    [InlineData("2606:4700:4700::1111")]  // Cloudflare DNS
    [InlineData("2001:4860:4860::8888")]  // Google DNS
    public async Task PublicV6_Accepted(string ip)
        => Assert.Equal(ip, await DetectV6(ip));

    [Theory]
    [InlineData("::1")]              // loopback
    [InlineData("fe80::1")]          // link-local
    [InlineData("fc00::1")]          // ULA
    [InlineData("fd12:3456::1")]     // ULA
    [InlineData("2001:db8::1")]      // documentation
    [InlineData("ff02::1")]          // multicast
    public async Task PrivateOrReservedV6_Rejected(string ip)
        => Assert.Null(await DetectV6(ip));

    [Fact]
    public async Task WrongFamily_Rejected()
    {
        // A v6 address from the v4 endpoint (and vice versa) must not be accepted.
        Assert.Null(await DetectV4("2606:4700:4700::1111"));
        Assert.Null(await DetectV6("8.8.8.8"));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public async Task NonAddressReply_Rejected(string reply)
        => Assert.Null(await DetectV4(reply));

    [Fact]
    public async Task InternalAddress_AcceptedWhenSkipDisabled()
    {
        // With SkipInternalAddresses off, a private address is published as is rather than rejected.
        var svc = new IPDetectionService(StubHttp.Always(HttpStatusCode.OK, "192.168.1.50"), NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://probe", IPv6DetectionUrl = string.Empty, SkipInternalAddresses = false };

        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);

        Assert.Equal("192.168.1.50", ip.IPv4);
    }

    [Fact]
    public async Task InternalAddress_RejectedByDefault()
    {
        // The default keeps the public only behavior, so a private address is rejected.
        var svc = new IPDetectionService(StubHttp.Always(HttpStatusCode.OK, "192.168.1.50"), NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://probe", IPv6DetectionUrl = string.Empty };

        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);

        Assert.Null(ip.IPv4);
    }
}
