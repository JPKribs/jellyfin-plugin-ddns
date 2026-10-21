using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Simply.com is a basic-auth GET to <c>/nic/update</c> whose first response token ("good"/"nochg")
/// decides success. Pins the token parsing, the query shape, and the optional Zone-to-domain scoping.
/// </summary>
public class SimplyComProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "account",
        Password = "apikey",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("good 1.2.3.4", true)]
    [InlineData("nochg 1.2.3.4", true)]
    [InlineData("badauth", false)]
    [InlineData("nohost", false)]
    [InlineData("abuse", false)]
    public async Task ParsesStatusToken(string body, bool expectSuccess)
    {
        var provider = new SimplyComProvider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<SimplyComProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure_EvenWithGoodBody()
    {
        var provider = new SimplyComProvider(StubHttp.Always(HttpStatusCode.Unauthorized, "good"), NullLogger<SimplyComProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_TargetsNicUpdateWithBasicAuth_NoDomainWithoutZone()
    {
        Uri? uri = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "good 1.2.3.4");
        });
        var provider = new SimplyComProvider(factory, NullLogger<SimplyComProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("dyndns.simply.com", uri!.Host);
        Assert.Equal("/nic/update", uri.AbsolutePath);
        Assert.Contains("hostname=home.example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("myip=1.2.3.4", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", uri.Query, StringComparison.Ordinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("account:apikey")), auth);
    }

    [Fact]
    public async Task ZoneSet_AddsTheDomainParameter()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "good 1.2.3.4");
        });
        var provider = new SimplyComProvider(factory, NullLogger<SimplyComProvider>.Instance);
        var record = Record();
        record.Zone = "example.com";

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("domain=example.com", uri!.Query, StringComparison.Ordinal);
    }
}
