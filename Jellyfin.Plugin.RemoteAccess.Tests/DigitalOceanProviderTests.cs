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
/// Covers the DigitalOcean flow: list the record by name and type with a Bearer token, PATCH its data
/// when the address changed, skip the PATCH when it is already current, and fail when the listing is
/// empty, ambiguous, or not the expected JSON shape.
/// </summary>
public class DigitalOceanProviderTests
{
    private const string ListOne = "{\"domain_records\":[{\"id\":42,\"data\":\"9.9.9.9\"}]}";

    private static DNSRecord Record() => new()
    {
        Hostname = "home",
        Zone = "example.com",
        Password = "do-token",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task Success_PatchesTheListedRecord()
    {
        var factory = StubHttp.Factory(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, ListOne)
            : (HttpStatusCode.OK, "{}"));
        var provider = new DigitalOceanProvider(factory, NullLogger<DigitalOceanProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("{\"id\":\"Unauthorized\",\"message\":\"Unable to authenticate you\"}")] // API error object
    [InlineData("{\"domain_records\":[]}")] // no matching record
    [InlineData("{\"domain_records\":[{\"id\":1,\"data\":\"9.9.9.9\"},{\"id\":2,\"data\":\"8.8.8.8\"}]}")] // ambiguous
    [InlineData("not json")]
    public async Task Listing200WithBadBody_IsFailure(string listBody)
    {
        var provider = new DigitalOceanProvider(
            StubHttp.Always(HttpStatusCode.OK, listBody),
            NullLogger<DigitalOceanProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AlreadyCurrent_SucceedsWithoutPatching()
    {
        var patched = false;
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                patched = true;
            }

            return (HttpStatusCode.OK, "{\"domain_records\":[{\"id\":42,\"data\":\"1.2.3.4\"}]}");
        });
        var provider = new DigitalOceanProvider(factory, NullLogger<DigitalOceanProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(patched);
    }

    [Fact]
    public async Task RequestShape_ListsThenPatchesWithBearerToken()
    {
        string? listUrl = null;
        string? listAuth = null;
        string? patchUrl = null;
        string? patchBody = null;

        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                listUrl = req.RequestUri!.AbsoluteUri;
                listAuth = req.Headers.Authorization?.ToString();
                return (HttpStatusCode.OK, ListOne);
            }

            patchUrl = req.RequestUri!.AbsoluteUri;
            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new DigitalOceanProvider(factory, NullLogger<DigitalOceanProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://api.digitalocean.com/v2/domains/example.com/records?name=home&type=A", listUrl);
        Assert.Equal("Bearer do-token", listAuth);
        Assert.Equal("https://api.digitalocean.com/v2/domains/example.com/records/42", patchUrl);
        Assert.Equal("{\"type\":\"A\",\"data\":\"1.2.3.4\"}", patchBody);
    }
}
