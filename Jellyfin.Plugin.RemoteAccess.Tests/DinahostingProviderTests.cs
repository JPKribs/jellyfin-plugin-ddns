using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers Dinahosting's host/domain split: a three-label hostname splits on the first dot, a bare
/// two-label hostname is the apex domain itself (not host "example" in domain "com"), and an explicit
/// Zone overrides the split. Error replies are parsed for the response code and message.
/// </summary>
public class DinahostingProviderTests
{
    private static DNSRecord Record(string hostname, string zone = "") => new()
    {
        Hostname = hostname,
        Zone = zone,
        Login = "user",
        Password = "pass",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    private static (DinahostingProvider Provider, Func<string?> Url) Tracked(string body = "Success")
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.ToString();
            return (HttpStatusCode.OK, body);
        });
        return (new DinahostingProvider(factory, NullLogger<DinahostingProvider>.Instance), () => url);
    }

    [Fact]
    public async Task ThreeLabelHostname_SplitsOnFirstDot()
    {
        var (provider, url) = Tracked();

        var result = await provider.UpdateAsync(Record("home.example.com"), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hostname=home&domain=example.com", url(), StringComparison.Ordinal);
        Assert.Contains("command=Domain_Zone_UpdateTypeA", url(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoLabelHostname_IsTreatedAsApexDomain()
    {
        var (provider, url) = Tracked();

        var result = await provider.UpdateAsync(Record("example.com"), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hostname=&domain=example.com", url(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitZone_OverridesTheFirstDotSplit()
    {
        var (provider, url) = Tracked();

        var result = await provider.UpdateAsync(
            Record("a.b.example.com", zone: "example.com"),
            new DetectedIP { IPv4 = "1.2.3.4" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hostname=a.b&domain=example.com", url(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorReply_IsParsedForCodeAndMessage()
    {
        var (provider, _) = Tracked("responseCode = 1002\nerrors_0_message = 'Invalid credentials'");

        var result = await provider.UpdateAsync(Record("home.example.com"), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("1002", result.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid credentials", result.Message, StringComparison.Ordinal);
    }
}
