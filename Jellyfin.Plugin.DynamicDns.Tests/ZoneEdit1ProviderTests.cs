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
/// ZoneEdit answers HTTP 200 with an XML-ish <c>&lt;SUCCESS&gt;</c>/<c>&lt;ERROR&gt;</c> tag, so the
/// tag (not the status code) decides the outcome, and ERROR 707 ("duplicate update") counts as success
/// to match ddclient. Pins that parsing plus the <c>/auth/dynamic.html</c> query and basic auth.
/// </summary>
public class ZoneEdit1ProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "user",
        Password = "token",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("<SUCCESS CODE=\"200\" TEXT=\"Update succeeded.\" IP=\"1.2.3.4\">", true)]
    [InlineData("<ERROR CODE=\"702\" TEXT=\"Update failed.\" ZONE=\"example.com\" HOST=\"home.example.com\">", false)]
    [InlineData("<ERROR CODE=\"707\" TEXT=\"Duplicate updates for the same host/ip, adjust client settings\" ZONE=\"example.com\">", true)]
    [InlineData("<ERROR CODE=\"708\" TEXT=\"Failed Login\">", false)]
    [InlineData("nothing recognizable", false)]
    public async Task ParsesStatusTag(string body, bool expectSuccess)
    {
        var provider = new ZoneEdit1Provider(StubHttp.Always(HttpStatusCode.OK, body), NullLogger<ZoneEdit1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure_EvenWithSuccessBody()
    {
        var provider = new ZoneEdit1Provider(
            StubHttp.Always(HttpStatusCode.Unauthorized, "<SUCCESS CODE=\"200\" TEXT=\"ok\" IP=\"1.2.3.4\">"),
            NullLogger<ZoneEdit1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_TargetsDynamicHtmlWithBasicAuth_NoZoneByDefault()
    {
        Uri? uri = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "<SUCCESS CODE=\"200\" TEXT=\"ok\" IP=\"1.2.3.4\">");
        });
        var provider = new ZoneEdit1Provider(factory, NullLogger<ZoneEdit1Provider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("dynamic.zoneedit.com", uri!.Host);
        Assert.Equal("/auth/dynamic.html", uri.AbsolutePath);
        Assert.Contains("host=home.example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("dnsto=1.2.3.4", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("zone=", uri.Query, StringComparison.Ordinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:token")), auth);
    }

    [Fact]
    public async Task ZoneSet_AddsTheZoneParameter()
    {
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "<SUCCESS CODE=\"200\" TEXT=\"ok\" IP=\"1.2.3.4\">");
        });
        var provider = new ZoneEdit1Provider(factory, NullLogger<ZoneEdit1Provider>.Instance);
        var record = Record();
        record.Zone = "example.com";

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("zone=example.com", uri!.Query, StringComparison.Ordinal);
    }
}
