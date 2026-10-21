using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers 1984 Hosting's freedns endpoint: the JSON <c>ok</c> field decides success (boolean or the
/// string "true"), so <c>ok:false</c> or a non-JSON body on HTTP 200 must be a failure.
/// </summary>
public class Hosting1984ProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "example.com",
        Password = "api-key",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("{\"ok\":true,\"msg\":\"Record created\",\"ip\":\"1.2.3.4\"}", true)]
    [InlineData("{\"ok\":\"true\",\"msg\":\"IP is unaltered\"}", true)] // string "true" is accepted
    [InlineData("{\"ok\":false,\"msg\":\"Invalid API key\"}", false)]
    [InlineData("{\"msg\":\"missing ok field\"}", false)]
    [InlineData("<html>maintenance</html>", false)] // 200 with a non-JSON body
    public async Task ParsesOkField(string body, bool expectSuccess)
    {
        var provider = new Hosting1984Provider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<Hosting1984Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new Hosting1984Provider(
            StubHttp.Always(HttpStatusCode.InternalServerError, "{\"ok\":true}"),
            NullLogger<Hosting1984Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_SendsApiKeyDomainAndIp()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "{\"ok\":true}");
        });
        var provider = new Hosting1984Provider(factory, NullLogger<Hosting1984Provider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://api.1984.is/1.0/freedns/?apikey=api-key&domain=example.com&ip=1.2.3.4", url);
    }
}
