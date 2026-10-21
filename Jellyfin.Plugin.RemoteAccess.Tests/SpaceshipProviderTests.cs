using System;
using System.Collections.Generic;
using System.Linq;
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
/// Covers Spaceship's non-destructive ordering: the replacement record is PUT before stale records are
/// deleted, so a failed write leaves the old address serving instead of removing the hostname entirely.
/// Also pins the already-set fast path, the resolved default TTL, and blank-zone apex inference.
/// </summary>
public class SpaceshipProviderTests
{
    private const string ListWithStale =
        "{\"items\":[{\"type\":\"A\",\"name\":\"home\",\"address\":\"9.9.9.9\"}]}";

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Login = "key",
        Password = "secret",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    private static (SpaceshipProvider Provider, List<(HttpMethod Method, string Body)> Writes) Tracked(
        string listBody,
        HttpStatusCode putStatus = HttpStatusCode.NoContent)
    {
        var writes = new List<(HttpMethod, string)>();
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, listBody);
            }

            var body = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            writes.Add((req.Method, body));
            return (req.Method == HttpMethod.Put ? putStatus : HttpStatusCode.NoContent, "{}");
        });
        return (new SpaceshipProvider(factory, NullLogger<SpaceshipProvider>.Instance), writes);
    }

    [Fact]
    public async Task StaleRecord_IsReplacedWithPutBeforeDelete()
    {
        var (provider, writes) = Tracked(ListWithStale);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, writes.Count);
        Assert.Equal(HttpMethod.Put, writes[0].Method);
        Assert.Contains("\"address\":\"1.2.3.4\"", writes[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"ttl\":1800", writes[0].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, writes[1].Method);
        Assert.Contains("\"address\":\"9.9.9.9\"", writes[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPut_LeavesExistingRecordsUndeleted()
    {
        var (provider, writes) = Tracked(ListWithStale, putStatus: HttpStatusCode.InternalServerError);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(writes, w => w.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RecordAlreadyCurrent_MakesNoWrites()
    {
        var (provider, writes) = Tracked("{\"items\":[{\"type\":\"A\",\"name\":\"home\",\"address\":\"1.2.3.4\"}]}");

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task BlankZoneTwoLabelHostname_IsTreatedAsApex()
    {
        var urls = new List<string>();
        string? putBody = null;
        var factory = StubHttp.Factory(req =>
        {
            urls.Add(req.RequestUri!.ToString());
            if (req.Method == HttpMethod.Put)
            {
                putBody = req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result;
            }

            return req.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, "{\"items\":[]}")
                : (HttpStatusCode.NoContent, "{}");
        });
        var provider = new SpaceshipProvider(factory, NullLogger<SpaceshipProvider>.Instance);
        var record = Record();
        record.Hostname = "example.com";
        record.Zone = string.Empty;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.All(urls, u => Assert.Contains("/dns/records/example.com", u, StringComparison.Ordinal));
        Assert.Contains("\"name\":\"@\"", putBody, StringComparison.Ordinal);
    }
}
