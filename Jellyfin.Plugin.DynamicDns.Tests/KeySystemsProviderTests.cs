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
/// Key-Systems replies with a "code = NNN" text body over HTTP 200, so only the literal
/// <c>code = 200</c> marker may count as success. Also pins the <c>/update.php</c> query shape.
/// </summary>
public class KeySystemsProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Password = "secret",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task Code200Body_IsSuccess()
    {
        var provider = new KeySystemsProvider(
            StubHttp.Always(HttpStatusCode.OK, "code = 200\ndescription = command completed successfully"),
            NullLogger<KeySystemsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ErrorCodeBodyWithHttp200_IsFailure()
    {
        // The service reports errors in the body while still answering HTTP 200.
        var provider = new KeySystemsProvider(
            StubHttp.Always(HttpStatusCode.OK, "code = 401\ndescription = authorization failed"),
            NullLogger<KeySystemsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Request_TargetsUpdatePhpWithHostPasswordAndIp()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        var factory = StubHttp.Factory(req =>
        {
            method = req.Method;
            uri = req.RequestUri;
            return (HttpStatusCode.OK, "code = 200");
        });
        var provider = new KeySystemsProvider(factory, NullLogger<KeySystemsProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("dynamicdns.key-systems.net", uri!.Host);
        Assert.Equal("/update.php", uri.AbsolutePath);
        Assert.Contains("hostname=home.example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("password=secret", uri.Query, StringComparison.Ordinal);
        Assert.Contains("ip=1.2.3.4", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPassword_FailsWithoutNetwork()
    {
        var provider = new KeySystemsProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<KeySystemsProvider>.Instance);
        var record = Record();
        record.Password = string.Empty;

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }
}
