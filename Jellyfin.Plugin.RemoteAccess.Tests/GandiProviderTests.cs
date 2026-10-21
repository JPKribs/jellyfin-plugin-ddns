using System;
using System.Collections.Generic;
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
/// Covers the Gandi LiveDNS flow: the default TTL sentinel (1) must resolve to Gandi's 300 second
/// minimum rather than being sent literally, a matching rrset skips the PUT, and a rejected PUT is
/// reported as failure.
/// </summary>
public class GandiProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Login = "token",
        Password = "secret",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task DefaultTtlSentinel_SendsGandisMinimumOf300()
    {
        var requests = new List<(HttpMethod Method, string Url, string Body)>();
        var factory = StubHttp.Factory(req =>
        {
            var body = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            requests.Add((req.Method, req.RequestUri!.ToString(), body));
            return req.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, "{\"rrset_values\":[\"9.9.9.9\"],\"rrset_ttl\":10800}")
                : (HttpStatusCode.Created, "{}");
        });
        var provider = new GandiProvider(factory, NullLogger<GandiProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        var put = Assert.Single(requests, r => r.Method == HttpMethod.Put);
        Assert.Contains("/livedns/domains/example.com/records/home/A", put.Url, StringComparison.Ordinal);
        Assert.Contains("\"rrset_ttl\":300", put.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RrsetAlreadyMatching_SkipsThePut()
    {
        var putCount = 0;
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                putCount++;
            }

            return (HttpStatusCode.OK, "{\"rrset_values\":[\"1.2.3.4\"],\"rrset_ttl\":300}");
        });
        var provider = new GandiProvider(factory, NullLogger<GandiProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, putCount);
    }

    [Fact]
    public async Task RejectedPut_IsReportedAsFailure()
    {
        var factory = StubHttp.Factory(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "{\"rrset_values\":[\"9.9.9.9\"]}")
            : (HttpStatusCode.BadRequest, "{\"message\":\"ttl: must be 300 or more\"}"));
        var provider = new GandiProvider(factory, NullLogger<GandiProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ttl: must be 300 or more", result.Message, StringComparison.Ordinal);
    }
}
