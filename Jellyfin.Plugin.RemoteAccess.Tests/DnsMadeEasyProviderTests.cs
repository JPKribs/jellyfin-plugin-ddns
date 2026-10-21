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
/// Covers DNS Made Easy's reply tokens: "success" and "error-record-ip-same" (no update required) are
/// both healthy outcomes, while an auth error is a failure. Treating the unchanged reply as failure
/// used to walk perfectly healthy records into backoff on force-interval pushes.
/// </summary>
public class DnsMadeEasyProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "12345",
        Login = "user@example.com",
        Password = "recordpass",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task SuccessReply_IsReportedAsSuccess()
    {
        var provider = new DnsMadeEasyProvider(
            StubHttp.Always(HttpStatusCode.OK, "success"),
            NullLogger<DnsMadeEasyProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task NoUpdateRequiredReply_IsReportedAsSuccess()
    {
        var provider = new DnsMadeEasyProvider(
            StubHttp.Always(HttpStatusCode.OK, "error-record-ip-same"),
            NullLogger<DnsMadeEasyProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("unchanged", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthErrorReply_IsReportedAsFailure()
    {
        var provider = new DnsMadeEasyProvider(
            StubHttp.Always(HttpStatusCode.OK, "error-auth"),
            NullLogger<DnsMadeEasyProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
