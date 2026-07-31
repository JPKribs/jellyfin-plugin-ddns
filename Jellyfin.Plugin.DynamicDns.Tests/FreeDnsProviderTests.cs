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
/// Covers FreeDNS (afraid.org) API v1: the SHA-1 credential list query, the pipe-separated record list,
/// the per-record update URL visit, and the family guard that keeps an A update off an IPv6 record.
/// </summary>
public class FreeDnsProviderTests
{
    // SHA-1 of "user|pass", the credential scheme mandated by the FreeDNS v1 API.
    private const string Sha = "8894721a433861735f4c2f52ff577ddd37279e24";

    private const string UpdateUrl = "https://freedns.afraid.org/dynamic/update.php?abc123";

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "user",
        Password = "pass",
        UpdateIPv4 = true,
    };

    private static bool IsListRequest(Uri uri)
        => uri.AbsoluteUri.Contains("action=getdyndns", StringComparison.Ordinal);

    [Fact]
    public async Task Success_VisitsThePerRecordUpdateUrl()
    {
        var factory = StubHttp.Factory(req => IsListRequest(req.RequestUri!)
            ? (HttpStatusCode.OK, "home.example.com|9.9.9.9|" + UpdateUrl)
            : (HttpStatusCode.OK, "Updated home.example.com to 1.2.3.4"));
        var provider = new FreeDnsProvider(factory, NullLogger<FreeDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UpdateError200_IsFailure()
    {
        var factory = StubHttp.Factory(req => IsListRequest(req.RequestUri!)
            ? (HttpStatusCode.OK, "home.example.com|9.9.9.9|" + UpdateUrl)
            : (HttpStatusCode.OK, "ERROR: Invalid update URL (2)"));
        var provider = new FreeDnsProvider(factory, NullLogger<FreeDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_ListUsesSha1AndUpdateAppendsAddress()
    {
        string? listUrl = null;
        string? updateUrl = null;
        var factory = StubHttp.Factory(req =>
        {
            if (IsListRequest(req.RequestUri!))
            {
                listUrl = req.RequestUri!.AbsoluteUri;
                return (HttpStatusCode.OK, "home.example.com|9.9.9.9|" + UpdateUrl);
            }

            updateUrl = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "Updated home.example.com to 1.2.3.4");
        });
        var provider = new FreeDnsProvider(factory, NullLogger<FreeDnsProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://freedns.afraid.org/api/?action=getdyndns&v=2&sha=" + Sha, listUrl);
        Assert.Equal(UpdateUrl + "&address=1.2.3.4", updateUrl);
    }

    [Fact]
    public async Task OnlyIPv6RecordExists_AUpdateFailsWithoutVisitingIt()
    {
        var visitedUpdateUrl = false;
        var factory = StubHttp.Factory(req =>
        {
            if (IsListRequest(req.RequestUri!))
            {
                return (HttpStatusCode.OK, "home.example.com|2001:db8::1|" + UpdateUrl);
            }

            visitedUpdateUrl = true;
            return (HttpStatusCode.OK, "Updated");
        });
        var provider = new FreeDnsProvider(factory, NullLogger<FreeDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(visitedUpdateUrl);
    }

    [Fact]
    public async Task AlreadyCurrent_SucceedsWithoutVisitingUpdateUrl()
    {
        var visitedUpdateUrl = false;
        var factory = StubHttp.Factory(req =>
        {
            if (IsListRequest(req.RequestUri!))
            {
                return (HttpStatusCode.OK, "home.example.com|1.2.3.4|" + UpdateUrl);
            }

            visitedUpdateUrl = true;
            return (HttpStatusCode.OK, "Updated");
        });
        var provider = new FreeDnsProvider(factory, NullLogger<FreeDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(visitedUpdateUrl);
    }
}
