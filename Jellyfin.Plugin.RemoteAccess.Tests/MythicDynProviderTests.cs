using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Mythic Beasts success is purely HTTP-status driven: a basic-auth POST to
/// <c>ipv4./ipv6.api.mythic-beasts.com/dns/v2/dynamic/{host}</c> per family, where the family prefix
/// forces the matching transport. Pins the status handling and the per-family host prefixing.
/// </summary>
public class MythicDynProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "keyid",
        Password = "keysecret",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task Http200_IsSuccess()
    {
        var provider = new MythicDynProvider(
            StubHttp.Always(HttpStatusCode.OK, "{\"message\":\"1 record updated\"}"),
            NullLogger<MythicDynProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new MythicDynProvider(
            StubHttp.Always(HttpStatusCode.Unauthorized, "{\"error\":\"authentication failed\"}"),
            NullLogger<MythicDynProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_PostsToIpv4PrefixedHostWithBasicAuth()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            method = req.Method;
            uri = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new MythicDynProvider(factory, NullLogger<MythicDynProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("ipv4.api.mythic-beasts.com", uri!.Host);
        Assert.Equal("/dns/v2/dynamic/home.example.com", uri.AbsolutePath);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("keyid:keysecret")), auth);
    }

    [Fact]
    public async Task Ipv6Family_TargetsIpv6PrefixedHost()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "{}");
        });
        var provider = new MythicDynProvider(factory, NullLogger<MythicDynProvider>.Instance);
        var record = Record();
        record.UpdateIPv4 = false;
        record.UpdateIPv6 = true;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv6 = "2001:db8::1" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ipv6.api.mythic-beasts.com", uri!.Host);
    }
}
