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
/// Covers easyDNS's HTML-wrapped status tokens: NOERROR/OK inside markup are success, a known error
/// token on HTTP 200 is failure, and the update URL carries the hostname, IP, and wildcard flag.
/// </summary>
public class EasyDnsProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "user",
        Password = "token",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task NoErrorTokenInsideHtml_IsReportedAsSuccess()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.ToString();
            return (HttpStatusCode.OK, "<html><body>NOERROR</body></html>");
        });
        var provider = new EasyDnsProvider(factory, NullLogger<EasyDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("/dyn/generic.php?hostname=home.example.com&myip=1.2.3.4&wildcard=OFF", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownErrorTokenWith200_IsReportedAsFailure()
    {
        var provider = new EasyDnsProvider(
            StubHttp.Always(HttpStatusCode.OK, "<html>NOACCESS</html>"),
            NullLogger<EasyDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("NOACCESS", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoWordIllegalInputStatus_IsRecognizedAsFailure()
    {
        var provider = new EasyDnsProvider(
            StubHttp.Always(HttpStatusCode.OK, "<p>ILLEGAL INPUT</p>"),
            NullLogger<EasyDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ILLEGAL INPUT", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownReply_IsReportedAsFailure()
    {
        var provider = new EasyDnsProvider(
            StubHttp.Always(HttpStatusCode.OK, "something unexpected"),
            NullLogger<EasyDnsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
