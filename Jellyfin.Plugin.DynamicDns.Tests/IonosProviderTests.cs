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
/// Covers the IONOS REST flow: list zones, fetch existing A/AAAA records, then PUT the matching record
/// or POST a new one. Pins the <c>X-API-Key</c> header and the TTL sentinel resolution: a record left
/// at the default <c>Ttl == 1</c> must be written with ttl 300.
/// </summary>
public class IonosProviderTests
{
    private const string Zones = "[{\"name\":\"example.com\",\"id\":\"zone1\",\"type\":\"NATIVE\"}]";
    private const string RecordsWithA = "{\"records\":[{\"id\":\"rec1\",\"name\":\"home.example.com\",\"type\":\"A\",\"content\":\"9.9.9.9\"}]}";
    private const string RecordsEmpty = "{\"records\":[]}";

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Password = "prefix.secret",
        UpdateIPv4 = true,
    };

    /// <summary>Routes zone/record lookups, and hands writes (PUT/POST) to <paramref name="onWrite"/>.</summary>
    private static IHttpClientFactory Routed(
        string recordsBody,
        Func<HttpRequestMessage, string, (HttpStatusCode Code, string Body)> onWrite)
        => StubHttp.Factory(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && string.Equals(path, "/dns/v1/zones", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, Zones);
            }

            if (req.Method == HttpMethod.Get && path.StartsWith("/dns/v1/zones/zone1", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, recordsBody);
            }

            if (req.Method == HttpMethod.Put || req.Method == HttpMethod.Post)
            {
                // Read the content inside the responder; the request is disposed after the send.
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return onWrite(req, body);
            }

            return (HttpStatusCode.NotFound, "{}");
        });

    [Fact]
    public async Task ExistingRecord_IsPutWithSentinelTtlResolvedTo300()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var factory = Routed(RecordsWithA, (req, b) =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = b;
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new IonosProvider(factory, NullLogger<IonosProvider>.Instance);

        // Ttl stays at the DNSRecord default of 1 (the "automatic" sentinel).
        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/dns/v1/zones/zone1/records/rec1", path);
        Assert.Contains("\"content\":\"1.2.3.4\"", body, StringComparison.Ordinal);
        Assert.Contains("\"ttl\":300", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRecord_IsPostedAsNewRecordArray()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var factory = Routed(RecordsEmpty, (req, b) =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = b;
            return (HttpStatusCode.Created, "{}");
        });
        var provider = new IonosProvider(factory, NullLogger<IonosProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/dns/v1/zones/zone1/records", path);
        Assert.StartsWith("[", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"home.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"A\"", body, StringComparison.Ordinal);
        Assert.Contains("\"ttl\":300", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitTtl_IsCarriedThrough()
    {
        string? body = null;
        var factory = Routed(RecordsWithA, (_, b) =>
        {
            body = b;
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new IonosProvider(factory, NullLogger<IonosProvider>.Instance);
        var record = Record();
        record.Ttl = 600;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"ttl\":600", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRejected_IsFailure()
    {
        var factory = Routed(RecordsWithA, (_, _) => (HttpStatusCode.BadRequest, "{\"message\":\"invalid record\"}"));
        var provider = new IonosProvider(factory, NullLogger<IonosProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task NoZoneCoversTheHost_FailsBeforeAnyWrite()
    {
        // The only zone does not contain the hostname, so no record write may happen.
        var provider = new IonosProvider(
            StubHttp.Always(HttpStatusCode.OK, "[{\"name\":\"other.net\",\"id\":\"zoneX\"}]"),
            NullLogger<IonosProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Requests_CarryTheApiKeyHeader()
    {
        string? apiKey = null;
        var factory = StubHttp.Factory(req =>
        {
            if (req.Headers.TryGetValues("X-API-Key", out var values))
            {
                apiKey = string.Join(",", values);
            }

            return (HttpStatusCode.OK, "[]");
        });
        var provider = new IonosProvider(factory, NullLogger<IonosProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("prefix.secret", apiKey);
    }
}
