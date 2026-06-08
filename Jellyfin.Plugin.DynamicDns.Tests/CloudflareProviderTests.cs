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
/// Covers the Cloudflare flow: look up the zone, find the existing record of the right family, and PATCH
/// it. A record of the wrong family must not be patched, and a missing zone fails before any record write.
/// </summary>
public class CloudflareProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Login = "token",
        Password = "secret-token",
        UpdateIPv4 = true,
    };

    private static IHttpClientFactory Routed(string recordType)
        => StubHttp.Factory(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Get && url.Contains("/zones/?name=", System.StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"result\":[{\"id\":\"zone1\",\"name\":\"example.com\"}]}");
            }

            if (req.Method == HttpMethod.Get && url.Contains("/dns_records?type=", System.StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"result\":[{\"id\":\"rec1\",\"name\":\"home.example.com\",\"type\":\"" + recordType + "\"}]}");
            }

            if (req.Method == HttpMethod.Patch)
            {
                return (HttpStatusCode.OK, "{\"result\":{\"id\":\"rec1\"}}");
            }

            return (HttpStatusCode.NotFound, "{}");
        });

    [Fact]
    public async Task Success_PatchesTheMatchingARecord()
    {
        var provider = new CloudflareProvider(Routed("A"), NullLogger<CloudflareProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task WrongFamilyRecord_IsNotPatched()
    {
        // The zone has only an AAAA record, but an A update is wanted. The family guard must skip it.
        var provider = new CloudflareProvider(Routed("AAAA"), NullLogger<CloudflareProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ZoneNotFound_FailsBeforeAnyRecordWrite()
    {
        var provider = new CloudflareProvider(
            StubHttp.Always(HttpStatusCode.OK, "{\"result\":[]}"),
            NullLogger<CloudflareProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
