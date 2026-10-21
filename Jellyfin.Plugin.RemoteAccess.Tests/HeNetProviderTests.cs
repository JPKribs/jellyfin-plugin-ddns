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
/// Covers Hurricane Electric: dyndns-style status parsing where "good" and "nochg" succeed and the
/// documented error tokens fail even on HTTP 200, and basic auth uses the hostname as the user.
/// </summary>
public class HeNetProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Password = "ddns-key",
        UpdateIPv4 = true,
    };

    [Theory]
    [InlineData("good 1.2.3.4", true)]
    [InlineData("nochg 1.2.3.4", true)]
    [InlineData("badauth", false)]
    [InlineData("nohost", false)]
    [InlineData("abuse", false)]
    public async Task ParsesStatusToken(string body, bool expectSuccess)
    {
        var provider = new HeNetProvider(
            StubHttp.Always(HttpStatusCode.OK, body),
            NullLogger<HeNetProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task HttpError_IsFailure()
    {
        var provider = new HeNetProvider(
            StubHttp.Always(HttpStatusCode.Unauthorized, "good 1.2.3.4"),
            NullLogger<HeNetProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_BasicAuthUsesHostnameAsUser()
    {
        string? url = null;
        string? auth = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "good 1.2.3.4");
        });
        var provider = new HeNetProvider(factory, NullLogger<HeNetProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal("https://dyn.dns.he.net/nic/update?hostname=home.example.com&myip=1.2.3.4", url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("home.example.com:ddns-key")), auth);
    }
}
