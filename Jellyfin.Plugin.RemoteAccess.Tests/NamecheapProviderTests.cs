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
/// Namecheap answers HTTP 200 with an XML interface-response; only <c>&lt;ErrCount&gt;0</c> is
/// success. Pins that parsing plus the host-label derivation: the domain suffix is stripped from the
/// hostname and a bare-domain hostname becomes the root record <c>@</c>.
/// </summary>
public class NamecheapProviderTests
{
    private const string SuccessXml =
        "<?xml version=\"1.0\"?><interface-response><Command>SETDNSHOST</Command><ErrCount>0</ErrCount><Done>true</Done></interface-response>";

    private const string ErrorXml =
        "<?xml version=\"1.0\"?><interface-response><ErrCount>1</ErrCount><errors><Err1>Domain name not found</Err1></errors><Done>true</Done></interface-response>";

    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "example.com",
        Password = "ddnspw",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task ZeroErrCount_IsSuccess()
    {
        var provider = new NamecheapProvider(StubHttp.Always(HttpStatusCode.OK, SuccessXml), NullLogger<NamecheapProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task NonZeroErrCountWithHttp200_IsFailure()
    {
        var provider = new NamecheapProvider(StubHttp.Always(HttpStatusCode.OK, ErrorXml), NullLogger<NamecheapProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_StripsDomainSuffixIntoHostLabel()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, SuccessXml);
        });
        var provider = new NamecheapProvider(factory, NullLogger<NamecheapProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("dynamicdns.park-your-domain.com", uri!.Host);
        Assert.Equal("/update", uri.AbsolutePath);
        Assert.Contains("host=home&", uri.Query, StringComparison.Ordinal);
        Assert.Contains("domain=example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("password=ddnspw", uri.Query, StringComparison.Ordinal);
        Assert.Contains("ip=1.2.3.4", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareDomainHostname_BecomesRootRecord()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, SuccessXml);
        });
        var provider = new NamecheapProvider(factory, NullLogger<NamecheapProvider>.Instance);
        var record = Record();
        record.Hostname = "example.com";

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("host=%40&", uri!.Query, StringComparison.Ordinal);
    }
}
