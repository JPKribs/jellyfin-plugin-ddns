using System;
using System.Collections.Generic;
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
/// Covers Yandex's record matching: the record to edit is found by fqdn AND type, so an IPv4 update
/// edits the A record even when another record type shares the same name, and a missing record of the
/// wanted type fails instead of overwriting whatever matched first.
/// </summary>
public class YandexProviderTests
{
    // The AAAA record deliberately comes first: a name-only match would edit record 111.
    private const string ListWithBothFamilies =
        "{\"success\":\"ok\",\"records\":["
        + "{\"fqdn\":\"home.example.com\",\"type\":\"AAAA\",\"record_id\":111},"
        + "{\"fqdn\":\"home.example.com\",\"type\":\"A\",\"record_id\":222}]}";

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "example.com",
        Password = "pddtoken",
        UpdateIPv4 = true,
        UpdateIPv6 = false
    };

    [Fact]
    public async Task IPv4Update_EditsTheARecord_NotTheFirstNameMatch()
    {
        var editBodies = new List<string>();
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, ListWithBothFamilies);
            }

            editBodies.Add(req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().Result);
            return (HttpStatusCode.OK, "{\"success\":\"ok\"}");
        });
        var provider = new YandexProvider(factory, NullLogger<YandexProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
        var body = Assert.Single(editBodies);
        Assert.Contains("record_id=222", body, StringComparison.Ordinal);
        Assert.Contains("content=1.2.3.4", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRecordOfWantedType_FailsInsteadOfEditingAnotherType()
    {
        var editCount = 0;
        var factory = StubHttp.Factory(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "{\"success\":\"ok\",\"records\":[{\"fqdn\":\"home.example.com\",\"type\":\"AAAA\",\"record_id\":111}]}");
            }

            editCount++;
            return (HttpStatusCode.OK, "{\"success\":\"ok\"}");
        });
        var provider = new YandexProvider(factory, NullLogger<YandexProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, editCount);
    }

    [Fact]
    public async Task EditRejected_IsReportedAsFailure()
    {
        var factory = StubHttp.Factory(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, ListWithBothFamilies)
            : (HttpStatusCode.OK, "{\"success\":\"error\",\"error\":\"bad_token\"}"));
        var provider = new YandexProvider(factory, NullLogger<YandexProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("bad_token", result.Message, StringComparison.Ordinal);
    }
}
