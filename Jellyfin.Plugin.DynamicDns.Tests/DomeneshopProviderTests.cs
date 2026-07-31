using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers Domeneshop's dyndns endpoint: basic-auth GET where any 2xx status is success (the body is not
/// parsed), any other status is failure, and missing credentials fail before any request is sent.
/// </summary>
public class DomeneshopProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "api-token",
        Password = "api-secret",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task Http204_IsSuccess()
    {
        var provider = new DomeneshopProvider(
            StubHttp.Always(HttpStatusCode.NoContent, string.Empty),
            NullLogger<DomeneshopProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Http404_IsFailure()
    {
        var provider = new DomeneshopProvider(
            StubHttp.Always(HttpStatusCode.NotFound, "Not found"),
            NullLogger<DomeneshopProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_GetWithBasicAuthAndMyIp()
    {
        string? url = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.NoContent, string.Empty);
        });
        var provider = new DomeneshopProvider(factory, NullLogger<DomeneshopProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://api.domeneshop.no/v0/dyndns/update?hostname=home.example.com&myip=1.2.3.4", url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("api-token:api-secret")), auth);
    }

    [Fact]
    public async Task MissingCredentials_FailsWithoutNetwork()
    {
        var provider = new DomeneshopProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<DomeneshopProvider>.Instance);
        var record = Record();
        record.Login = string.Empty;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
