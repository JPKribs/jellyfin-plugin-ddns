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
/// Covers NS1's create-versus-update split: a record that does not exist yet (GET 404) is created with
/// a PUT whose body carries the required zone, domain, and type fields, while an existing record is
/// updated with a POST that carries only the answers. Missing those create fields used to make
/// first-time setup fail on every run.
/// </summary>
public class Ns1ProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "apikey",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task MissingRecord_IsCreatedWithZoneDomainAndType()
    {
        var writes = new List<(HttpMethod Method, string Body)>();
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.NotFound, "{\"message\":\"record not found\"}");
            }

            writes.Add((req.Method, req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result));
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new Ns1Provider(factory, NullLogger<Ns1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        var (method, body) = Assert.Single(writes);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Contains("\"zone\":\"example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"home.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"A\"", body, StringComparison.Ordinal);
        Assert.Contains("\"ttl\":300", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingRecord_IsUpdatedWithAnswersOnly()
    {
        var writes = new List<(HttpMethod Method, string Body)>();
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "{\"type\":\"A\",\"answers\":[{\"answer\":[\"9.9.9.9\"]}]}");
            }

            writes.Add((req.Method, req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result));
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new Ns1Provider(factory, NullLogger<Ns1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        var (method, body) = Assert.Single(writes);
        Assert.Equal(HttpMethod.Post, method);
        Assert.DoesNotContain("\"zone\"", body, StringComparison.Ordinal);
        Assert.Contains("\"answer\":[\"1.2.3.4\"]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiError_IsReportedAsFailureWithDetail()
    {
        var factory = StubHttp.Factory(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "{\"type\":\"A\"}")
            : (HttpStatusCode.BadRequest, "{\"message\":\"invalid answers\"}"));
        var provider = new Ns1Provider(factory, NullLogger<Ns1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("invalid answers", result.Message, StringComparison.Ordinal);
    }
}
