using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Sitelutions updates by numeric record id via a GET to <c>/dnsup</c> and only a body containing
/// "success" passes. Pins that parsing, the id/user/pass/ip query shape, and the TTL sentinel rule:
/// no ttl parameter is sent unless the user raised it above 1.
/// </summary>
public class SitelutionsProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "123456",
        Login = "me@example.com",
        Password = "pw",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task SuccessBody_IsSuccess()
    {
        var provider = new SitelutionsProvider(StubHttp.Always(HttpStatusCode.OK, "success"), NullLogger<SitelutionsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ErrorBodyWithHttp200_IsFailure()
    {
        var provider = new SitelutionsProvider(
            StubHttp.Always(HttpStatusCode.OK, "failure (Invalid IP address given.)"),
            NullLogger<SitelutionsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_CarriesIdUserPassAndIp_NoTtlAtSentinel()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "success");
        });
        var provider = new SitelutionsProvider(factory, NullLogger<SitelutionsProvider>.Instance);

        // Ttl stays at the DNSRecord default of 1, which means "let the provider decide".
        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("api2.sitelutions.com", uri!.Host);
        Assert.Equal("/dnsup", uri.AbsolutePath);
        Assert.Contains("id=123456", uri.Query, StringComparison.Ordinal);
        Assert.Contains("user=me%40example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("pass=pw", uri.Query, StringComparison.Ordinal);
        Assert.Contains("ip=1.2.3.4", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("ttl=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TtlAboveSentinel_IsSent()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "success");
        });
        var provider = new SitelutionsProvider(factory, NullLogger<SitelutionsProvider>.Instance);
        var record = Record();
        record.Ttl = 600;

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("ttl=600", uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRecordId_FailsWithoutNetwork()
    {
        var provider = new SitelutionsProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<SitelutionsProvider>.Instance);
        var record = Record();
        record.Hostname = string.Empty;

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }
}
