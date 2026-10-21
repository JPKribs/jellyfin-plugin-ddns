using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
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
        var record = new DNSRecord { Hostname = "home.example.com", Zone = "", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DuckDns_MissingToken_FailsWithoutNetwork()
    {
        var provider = new DuckDnsProvider(NoNetworkFactory(), NullLogger<DuckDnsProvider>.Instance);
        var record = new DNSRecord { Hostname = "mysub", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DynDns2_MissingLogin_FailsWithoutNetwork()
    {
        var provider = new DynDns2Provider(NoNetworkFactory(), NullLogger<DynDns2Provider>.Instance);
        var record = new DNSRecord { Hostname = "home.example.com", Login = "", Password = "" };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task NoIp_MissingHostname_FailsWithoutNetwork()
    {
        var provider = new NoIpProvider(NoNetworkFactory(), NullLogger<NoIpProvider>.Instance);
        var record = new DNSRecord { Hostname = "", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DslReports1_MissingHostname_FailsWithoutNetwork()
    {
        var provider = new DslReports1Provider(NoNetworkFactory(), NullLogger<DslReports1Provider>.Instance);
        var record = new DNSRecord { Hostname = "", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DynDns1_MissingHostname_FailsWithoutNetwork()
    {
        var provider = new DynDns1Provider(NoNetworkFactory(), NullLogger<DynDns1Provider>.Instance);
        var record = new DNSRecord { Hostname = "", Login = "u", Password = "p", UpdateIPv4 = true };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void DNSUpdateResult_OkAndFail_CarryState()
    {
        Assert.True(DNSUpdateResult.Ok("done").Success);
        Assert.Equal("done", DNSUpdateResult.Ok("done").Message);
        Assert.False(DNSUpdateResult.Fail("nope").Success);
    }

    [Fact]
    public void Provider_KindMatchesImplementation()
    {
        Assert.Equal(DNSProviderKind.Cloudflare, new CloudflareProvider(NoNetworkFactory(), NullLogger<CloudflareProvider>.Instance).Kind);
        Assert.Equal(DNSProviderKind.DuckDns, new DuckDnsProvider(NoNetworkFactory(), NullLogger<DuckDnsProvider>.Instance).Kind);
        Assert.Equal(DNSProviderKind.DynDns2, new DynDns2Provider(NoNetworkFactory(), NullLogger<DynDns2Provider>.Instance).Kind);
        Assert.Equal(DNSProviderKind.NoIp, new NoIpProvider(NoNetworkFactory(), NullLogger<NoIpProvider>.Instance).Kind);
        Assert.Equal(DNSProviderKind.Dynu, new DynuProvider(NoNetworkFactory(), NullLogger<DynuProvider>.Instance).Kind);
    }
}
