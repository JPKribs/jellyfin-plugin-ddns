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
/// Infomaniak speaks the dyndns text protocol ("good"/"nochg" status tokens) over a basic-auth GET to
/// <c>/nic/update</c>. Pins the status-token parsing and the request shape (endpoint, query, basic auth).
/// </summary>
public class InfomaniakProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "user",
        Password = "pass",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("good 1.2.3.4", true)]
    [InlineData("nochg 1.2.3.4", true)]
    [InlineData("nohost", false)]
    [InlineData("badauth", false)]
    [InlineData("some unexpected reply", false)]
    public async Task ParsesStatusToken(string body, bool expectSuccess)
    {
        var provider = new InfomaniakProvider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<InfomaniakProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure_EvenWithGoodBody()
    {
        var provider = new InfomaniakProvider(StubHttp.Always(HttpStatusCode.Unauthorized, "good 1.2.3.4"), NullLogger<InfomaniakProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_TargetsNicUpdateWithBasicAuth()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            method = req.Method;
            uri = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "good 1.2.3.4");
        });
        var provider = new InfomaniakProvider(factory, NullLogger<InfomaniakProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("infomaniak.com", uri!.Host);
        Assert.Equal("/nic/update", uri.AbsolutePath);
        Assert.Contains("hostname=home.example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("myip=1.2.3.4", uri.Query, StringComparison.Ordinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass")), auth);
    }

    [Fact]
    public async Task MissingCredentials_FailsWithoutNetwork()
    {
        var provider = new InfomaniakProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<InfomaniakProvider>.Instance);
        var record = Record();
        record.Login = string.Empty;

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }
}
