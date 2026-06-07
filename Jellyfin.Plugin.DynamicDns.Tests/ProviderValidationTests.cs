using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Each provider must reject incomplete records before making any network call.
/// The stub factory throws if a client is ever created, proving the short-circuit.
/// </summary>
public class ProviderValidationTests
{
    private static IHttpClientFactory NoNetworkFactory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => throw new Xunit.Sdk.XunitException("Provider attempted a network call during validation."));
        return factory;
    }

    [Fact]
    public async Task Cloudflare_MissingCredentials_FailsWithoutNetwork()
    {
        var provider = new CloudflareProvider(NoNetworkFactory(), NullLogger<CloudflareProvider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Zone = "", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIp { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DuckDns_MissingToken_FailsWithoutNetwork()
    {
        var provider = new DuckDnsProvider(NoNetworkFactory(), NullLogger<DuckDnsProvider>.Instance);
        var record = new DnsRecord { Hostname = "mysub", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIp { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DynDns2_MissingLogin_FailsWithoutNetwork()
    {
        var provider = new DynDns2Provider(NoNetworkFactory(), NullLogger<DynDns2Provider>.Instance);
        var record = new DnsRecord { Hostname = "home.example.com", Login = "", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIp { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void DnsUpdateResult_OkAndFail_CarryState()
    {
        Assert.True(DnsUpdateResult.Ok("done").Success);
        Assert.Equal("done", DnsUpdateResult.Ok("done").Message);
        Assert.False(DnsUpdateResult.Fail("nope").Success);
    }

    [Fact]
    public void Provider_KindMatchesImplementation()
    {
        Assert.Equal(DnsProviderKind.Cloudflare, new CloudflareProvider(NoNetworkFactory(), NullLogger<CloudflareProvider>.Instance).Kind);
        Assert.Equal(DnsProviderKind.DuckDns, new DuckDnsProvider(NoNetworkFactory(), NullLogger<DuckDnsProvider>.Instance).Kind);
        Assert.Equal(DnsProviderKind.DynDns2, new DynDns2Provider(NoNetworkFactory(), NullLogger<DynDns2Provider>.Instance).Kind);
        Assert.Equal(DnsProviderKind.NoIp, new NoIpProvider(NoNetworkFactory(), NullLogger<NoIpProvider>.Instance).Kind);
        Assert.Equal(DnsProviderKind.Dynu, new DynuProvider(NoNetworkFactory(), NullLogger<DynuProvider>.Instance).Kind);
    }
}
