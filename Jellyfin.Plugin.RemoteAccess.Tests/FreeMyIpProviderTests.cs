using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers freemyip.com: success is the OK status token (not a substring, so "TOKEN" must fail), a single
/// family pins <c>myip</c> explicitly, and a dual-stack record sends one request with no <c>myip</c> so
/// the service updates both families from the connection.
/// </summary>
public class FreeMyIpProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "example.freemyip.com",
        Password = "tok",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("OK", true)]
    [InlineData("OK\nUpdated example.freemyip.com", true)]
    [InlineData("ERROR: token is invalid", false)]
    [InlineData("TOKEN", false)] // contains "OK" as a substring but is not the status token
    public async Task ParsesStatusToken(string body, bool expectSuccess)
    {
        var provider = new FreeMyIpProvider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<FreeMyIpProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task RequestShape_SingleFamilyPinsMyIp()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "OK");
        });
        var provider = new FreeMyIpProvider(factory, NullLogger<FreeMyIpProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://freemyip.com/update?token=tok&domain=example.freemyip.com&myip=1.2.3.4", url);
    }

    [Fact]
    public async Task DualStack_SendsOneRequestWithoutMyIp()
    {
        var requests = 0;
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            requests++;
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "OK");
        });
        var provider = new FreeMyIpProvider(factory, NullLogger<FreeMyIpProvider>.Instance);
        var record = Record();
        record.UpdateIPv6 = true;

        var result = await provider.UpdateAsync(
            record,
            new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, requests);
        Assert.Equal("https://freemyip.com/update?token=tok&domain=example.freemyip.com", url);
    }
}
