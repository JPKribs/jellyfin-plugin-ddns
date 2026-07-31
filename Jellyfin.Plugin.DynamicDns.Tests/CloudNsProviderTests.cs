using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers ClouDNS: the DynURL from the Password field is fetched with <c>proxy=1</c> appended and the
/// desired address in <c>X-Forwarded-For</c>. Only the two documented error replies fail; anything else
/// is success because ClouDNS does not document a success body.
/// </summary>
public class CloudNsProviderTests
{
    private const string DynUrl = "https://ipv4.cloudns.net/api/dynamicURL/?q=secret-key";

    private static DNSRecord Record() => new()
    {
        Password = DynUrl,
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("1.2.3.4", true)] // any non-error reply is success
    [InlineData("The record's key is wrong!", false)]
    [InlineData("Invalid request.", false)]
    public async Task ParsesReply(string body, bool expectSuccess)
    {
        var provider = new CloudNsProvider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<CloudNsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new CloudNsProvider(
            StubHttp.Always(HttpStatusCode.BadGateway, "1.2.3.4"),
            NullLogger<CloudNsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_AppendsProxyAndSendsXForwardedFor()
    {
        string? url = null;
        string? forwardedFor = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            forwardedFor = req.Headers.TryGetValues("X-Forwarded-For", out var values)
                ? values.First()
                : null;
            return (HttpStatusCode.OK, "1.2.3.4");
        });
        var provider = new CloudNsProvider(factory, NullLogger<CloudNsProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(DynUrl + "&proxy=1", url);
        Assert.Equal("1.2.3.4", forwardedFor);
    }

    [Fact]
    public async Task NonHttpDynUrl_FailsWithoutNetwork()
    {
        var provider = new CloudNsProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<CloudNsProvider>.Instance);
        var record = Record();
        record.Password = "not-a-url";

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
