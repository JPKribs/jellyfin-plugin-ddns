using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers Directnic: the per-family gateway URL (Login for A, Password for AAAA) gets the address as a
/// <c>data</c> parameter, and only a JSON body with <c>result:"success"</c> counts as success.
/// </summary>
public class DirectnicProviderTests
{
    private const string GatewayUrl = "https://directnic.com/dns/gateway/abc123/";

    private static DNSRecord Record() => new()
    {
        Login = GatewayUrl,
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("{\"result\":\"success\",\"message\":\"Your record has been updated.\"}", true)]
    [InlineData("{\"result\":\"error\",\"message\":\"invalid token\"}", false)]
    [InlineData("{\"message\":\"no result field\"}", false)]
    [InlineData("<html>not json</html>", false)] // 200 with a non-JSON body
    public async Task ParsesResultField(string body, bool expectSuccess)
    {
        var provider = new DirectnicProvider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<DirectnicProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task RequestShape_AppendsDataParameterToGatewayUrl()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "{\"result\":\"success\"}");
        });
        var provider = new DirectnicProvider(factory, NullLogger<DirectnicProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(GatewayUrl + "?data=1.2.3.4", url);
    }

    [Fact]
    public async Task BareGatewayPath_IsAnchoredToTheDefaultHost()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "{\"result\":\"success\"}");
        });
        var provider = new DirectnicProvider(factory, NullLogger<DirectnicProvider>.Instance);
        var record = Record();
        record.Login = "dns/gateway/abc123";

        await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://directnic.com/dns/gateway/abc123?data=1.2.3.4", url);
    }

    [Fact]
    public async Task IPv6WithoutGatewayUrl_FailsWithoutNetwork()
    {
        var provider = new DirectnicProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<DirectnicProvider>.Instance);
        var record = new DNSRecord { Login = GatewayUrl, UpdateIPv4 = false, UpdateIPv6 = true };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv6 = "2001:db8::1" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
