using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Request-shaping tests for Porkbun. Porkbun returns HTTP 200 with a JSON <c>status</c> field, so a
/// failed edit (<c>"status":"ERROR"</c>) must be reported as failure, not success.
/// </summary>
public class PorkbunProviderTests
{
    private const string RetrieveOk =
        "{\"status\":\"SUCCESS\",\"records\":[{\"id\":\"1\",\"content\":\"0.0.0.0\",\"ttl\":\"600\"}]}";

    private static PorkbunProvider Provider(string editResponse)
    {
        var factory = StubHttp.Factory(req =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("retrieveByNameType", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, RetrieveOk)
                : (HttpStatusCode.OK, editResponse);
        });
        return new PorkbunProvider(factory, NullLogger<PorkbunProvider>.Instance);
    }

    private static DnsRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "apikey",
        Password = "secretapikey",
        UpdateIPv4 = true
    };

    [Fact]
    public async Task Edit_ReturnsErrorStatusWith200_IsReportedAsFailure()
    {
        var provider = Provider("{\"status\":\"ERROR\",\"message\":\"nope\"}");

        var result = await provider.UpdateAsync(Record(), new DetectedIp { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Edit_ReturnsSuccessStatus_IsReportedAsSuccess()
    {
        var provider = Provider("{\"status\":\"SUCCESS\"}");

        var result = await provider.UpdateAsync(Record(), new DetectedIp { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }
}
