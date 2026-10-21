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
/// Covers Hetzner's asynchronous action flow: an action that reports success is success, one that
/// reports an error is failure, and one still pending after the short polls counts as accepted rather
/// than a false timeout that would feed the backoff. The create body must omit the TTL for the
/// sentinel so the zone default applies.
/// </summary>
public class HetznerProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Password = "token",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    private static HetznerProvider Provider(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
        => new(StubHttp.Factory(responder), NullLogger<HetznerProvider>.Instance);

    [Fact]
    public async Task ExistingRrset_SetRecordsActionSucceeds()
    {
        var provider = Provider(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/actions/42", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"action\":{\"id\":42,\"status\":\"success\"}}");
            }

            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "{\"rrset\":{\"name\":\"home\"}}");
            }

            return (HttpStatusCode.Created, "{\"action\":{\"id\":42,\"status\":\"running\"}}");
        });

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ActionReportsError_IsReportedAsFailure()
    {
        var provider = Provider(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/actions/42", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"action\":{\"id\":42,\"status\":\"error\",\"error\":{\"message\":\"rate limited\"}}}");
            }

            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "{\"rrset\":{\"name\":\"home\"}}");
            }

            return (HttpStatusCode.Created, "{\"action\":{\"id\":42,\"status\":\"running\"}}");
        });

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rate limited", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionStillPendingAfterPolls_CountsAsAccepted()
    {
        var provider = Provider(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/actions/42", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"action\":{\"id\":42,\"status\":\"running\"}}");
            }

            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "{\"rrset\":{\"name\":\"home\"}}");
            }

            return (HttpStatusCode.Created, "{\"action\":{\"id\":42,\"status\":\"running\"}}");
        });

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("still applying", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithDefaultTtlSentinel_OmitsTheTtlField()
    {
        var creates = new List<string>();
        var provider = Provider(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/actions/42", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"action\":{\"id\":42,\"status\":\"success\"}}");
            }

            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.NotFound, "{}");
            }

            creates.Add(req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result);
            return (HttpStatusCode.Created, "{\"action\":{\"id\":42,\"status\":\"running\"}}");
        });

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        var body = Assert.Single(creates);
        Assert.DoesNotContain("\"ttl\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"home\"", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"1.2.3.4\"", body, StringComparison.Ordinal);
    }
}
