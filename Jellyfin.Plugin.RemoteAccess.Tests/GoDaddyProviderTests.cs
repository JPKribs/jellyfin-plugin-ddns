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
/// Covers GoDaddy: the default TTL sentinel (1) must resolve to GoDaddy's 600 second minimum rather
/// than being sent literally (which the API rejects with a 422 on every run), and a rejected PUT is a
/// failure with the described cause.
/// </summary>
public class GoDaddyProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Login = "key",
        Password = "secret",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task DefaultTtlSentinel_SendsGoDaddysMinimumOf600()
    {
        string? putUrl = null;
        string? putBody = null;
        var factory = StubHttp.Factory(req =>
        {
            putUrl = req.RequestUri!.ToString();
            putBody = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new GoDaddyProvider(factory, NullLogger<GoDaddyProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("/example.com/records/A/home", putUrl, StringComparison.Ordinal);
        Assert.Contains("\"ttl\":600", putBody, StringComparison.Ordinal);
        Assert.Contains("\"data\":\"1.2.3.4\"", putBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserSetTtl_IsSentAsEntered()
    {
        string? putBody = null;
        var factory = StubHttp.Factory(req =>
        {
            putBody = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new GoDaddyProvider(factory, NullLogger<GoDaddyProvider>.Instance);
        var record = Record();
        record.Ttl = 3600;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"ttl\":3600", putBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPut_IsReportedAsFailure()
    {
        var provider = new GoDaddyProvider(
            StubHttp.Always(HttpStatusCode.UnprocessableEntity, "{}"),
            NullLogger<GoDaddyProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("422", result.Message, StringComparison.Ordinal);
    }
}
