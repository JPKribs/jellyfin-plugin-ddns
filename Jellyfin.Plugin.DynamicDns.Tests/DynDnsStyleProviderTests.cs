using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Request-shaping tests for the text-protocol providers (OVH, DDNSS, ChangeIP). They report success
/// from the response body, so these pin the success/failure parsing against representative replies.
/// </summary>
public class DynDnsStyleProviderTests
{
    private static readonly DetectedIp V4 = new() { IPv4 = "1.2.3.4" };

    // MARK: OVH (dyndns2 "good" / "nochg")

    [Theory]
    [InlineData("good 1.2.3.4", true)]
    [InlineData("nochg 1.2.3.4", true)]
    [InlineData("badauth", false)]
    [InlineData("nohost", false)]
    public async Task Ovh_ParsesDynDnsReply(string body, bool expectSuccess)
    {
        var provider = new OvhProvider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<OvhProvider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task Ovh_HttpError_IsFailure()
    {
        var provider = new OvhProvider(StubHttp.Always(HttpStatusCode.Unauthorized, "nope"), NullLogger<OvhProvider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    // MARK: DDNSS.de ("good")

    [Theory]
    [InlineData("good", true)]
    [InlineData("Updated 1 hostname good", true)]
    [InlineData("badysn", false)]
    [InlineData("Error: bad key", false)]
    public async Task Ddnss_ParsesReply(string body, bool expectSuccess)
    {
        var provider = new DdnssProvider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<DdnssProvider>.Instance);
        var record = new DnsRecord { Hostname = "host", Password = "key", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    // MARK: ChangeIP ("success")

    [Theory]
    [InlineData("Successful update", true)]
    [InlineData("200 Successful Update", true)]
    [InlineData("Authentication failed", false)]
    public async Task ChangeIp_ParsesReply(string body, bool expectSuccess)
    {
        var provider = new ChangeIpProvider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<ChangeIpProvider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    // MARK: shared validation short-circuit (no network)

    [Fact]
    public async Task Ovh_MissingCredentials_FailsWithoutNetwork()
    {
        // Throws if any request is actually sent — proves the early-out before any network call.
        var provider = new OvhProvider(StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")), NullLogger<OvhProvider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Login = "", Password = "", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }
}
