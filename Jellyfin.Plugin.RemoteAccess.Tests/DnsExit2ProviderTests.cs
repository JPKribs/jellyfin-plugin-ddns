using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers DNSExit's JSON status codes (0 is success, everything else a failure even on HTTP 200) and
/// the TTL sentinel resolving to the protocol default of 5.
/// </summary>
public class DnsExit2ProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Password = "apikey",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task CodeZero_IsSuccess_AndDefaultTtlSentinelSendsFive()
    {
        string? postBody = null;
        var factory = StubHttp.Factory(req =>
        {
            postBody = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, "{\"code\":0,\"message\":\"Success\"}");
        });
        var provider = new DnsExit2Provider(factory, NullLogger<DnsExit2Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"ttl\":5", postBody, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"1.2.3.4\"", postBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthErrorCodeWith200_IsReportedAsFailure()
    {
        var provider = new DnsExit2Provider(
            StubHttp.Always(HttpStatusCode.OK, "{\"code\":2,\"message\":\"Invalid API key\"}"),
            NullLogger<DnsExit2Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("badauth", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserSetTtl_IsSentAsEntered()
    {
        string? postBody = null;
        var factory = StubHttp.Factory(req =>
        {
            postBody = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, "{\"code\":0,\"message\":\"Success\"}");
        });
        var provider = new DnsExit2Provider(factory, NullLogger<DnsExit2Provider>.Instance);
        var record = Record();
        record.Ttl = 10;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"ttl\":10", postBody, StringComparison.Ordinal);
    }
}
