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
/// Covers INWX: dyndns2-style status parsing over basic auth, both addresses in one request via
/// <c>myip</c>/<c>myipv6</c>, and the guard that refuses an IPv6-only update because INWX requires the
/// IPv4 address alongside it.
/// </summary>
public class InwxProviderTests
{
    private static DNSRecord Record() => new()
    {
        Login = "user",
        Password = "pass",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("good 1.2.3.4", true)]
    [InlineData("nochg 1.2.3.4", true)]
    [InlineData("badauth", false)]
    [InlineData("!yours", false)]
    [InlineData("abuse", false)]
    public async Task ParsesStatusToken(string body, bool expectSuccess)
    {
        var provider = new InwxProvider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<InwxProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new InwxProvider(
            StubHttp.Always(HttpStatusCode.Unauthorized, "good"),
            NullLogger<InwxProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_DualStackSendsBothAddressesInOneRequest()
    {
        var requests = 0;
        string? url = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            requests++;
            url = req.RequestUri!.AbsoluteUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "good");
        });
        var provider = new InwxProvider(factory, NullLogger<InwxProvider>.Instance);
        var record = Record();
        record.UpdateIPv6 = true;

        var result = await provider.UpdateAsync(
            record,
            new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, requests);
        Assert.Equal("https://dyndns.inwx.com/nic/update?myip=1.2.3.4&myipv6=2001%3Adb8%3A%3A1", url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass")), auth);
    }

    [Fact]
    public async Task IPv6WithoutIPv4_FailsWithoutNetwork()
    {
        var provider = new InwxProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<InwxProvider>.Instance);
        var record = new DNSRecord { Login = "user", Password = "pass", UpdateIPv4 = false, UpdateIPv6 = true };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv6 = "2001:db8::1" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
