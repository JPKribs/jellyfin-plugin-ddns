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
/// regfish.de takes a single GET carrying both families and reports the outcome in the body: only a
/// reply containing "success" passes. Pins that parsing, the query shape (fqdn/forcehost/token/ipv4/ipv6),
/// and the no-address early out.
/// </summary>
public class RegfishDeProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Password = "updatetoken",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task SuccessBody_IsSuccess()
    {
        var provider = new RegfishDeProvider(
            StubHttp.Always(HttpStatusCode.OK, "success|100|the update was successful"),
            NullLogger<RegfishDeProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ErrorBodyWithHttp200_IsFailure()
    {
        var provider = new RegfishDeProvider(
            StubHttp.Always(HttpStatusCode.OK, "ERR|401|authorization failed"),
            NullLogger<RegfishDeProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new RegfishDeProvider(
            StubHttp.Always(HttpStatusCode.Forbidden, "success"),
            NullLogger<RegfishDeProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_CarriesBothFamiliesInOneQuery()
    {
        var requests = 0;
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            requests++;
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "success");
        });
        var provider = new RegfishDeProvider(factory, NullLogger<RegfishDeProvider>.Instance);
        var record = Record();
        record.UpdateIPv6 = true;

        var result = await provider.UpdateAsync(
            record,
            new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, requests);
        Assert.Equal("dyndns.regfish.de", uri!.Host);
        Assert.Contains("fqdn=home.example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("forcehost=1", uri.Query, StringComparison.Ordinal);
        Assert.Contains("token=updatetoken", uri.Query, StringComparison.Ordinal);
        Assert.Contains("ipv4=1.2.3.4", uri.Query, StringComparison.Ordinal);
        Assert.Contains("ipv6=2001%3Adb8%3A%3A1", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMatchingAddress_FailsWithoutNetwork()
    {
        // IPv4 updates are enabled but detection produced no IPv4, so nothing may be sent.
        var provider = new RegfishDeProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<RegfishDeProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv6 = "2001:db8::1" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
