using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// NearlyFreeSpeech.NET has no update call: the provider lists the existing A record, removes it if
/// present, and re-adds the new address, all as signed form POSTs under <c>/dns/{zone}/</c>. Pins the
/// list/remove/add sequence, the form bodies (including the 300s TTL default), the
/// <c>X-NFSN-Authentication</c> header shape, and the IPv4-only and zone-membership guards.
/// </summary>
public class NfsnProviderTests
{
    private static readonly DetectedIP V4 = new() { IPv4 = "1.2.3.4" };

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Zone = "example.com",
        Login = "member",
        Password = "apikey",
        UpdateIPv4 = true,
    };

    /// <summary>Routes the three RR endpoints and records each call's path and form body.</summary>
    private static IHttpClientFactory Routed(
        string listBody,
        List<(string Path, string Body)> calls,
        (HttpStatusCode Code, string Body)? addReply = null)
        => StubHttp.Factory(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            // Read the content inside the responder; the request is disposed after the send.
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            calls.Add((path, body));

            if (path.EndsWith("/listRRs", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, listBody);
            }

            if (path.EndsWith("/removeRR", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, string.Empty);
            }

            if (path.EndsWith("/addRR", StringComparison.Ordinal))
            {
                return addReply ?? (HttpStatusCode.OK, string.Empty);
            }

            return (HttpStatusCode.NotFound, string.Empty);
        });

    [Fact]
    public async Task NoExistingRecord_AddsWithoutRemoving()
    {
        var calls = new List<(string Path, string Body)>();
        var provider = new NfsnProvider(Routed("[]", calls), NullLogger<NfsnProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, calls.Count);
        Assert.Equal("/dns/example.com/listRRs", calls[0].Path);
        Assert.Equal("name=home&type=A", calls[0].Body);
        Assert.Equal("/dns/example.com/addRR", calls[1].Path);
        // Ttl left at the 1-second sentinel resolves to the ddclient NFSN default of 300.
        Assert.Equal("name=home&type=A&data=1.2.3.4&ttl=300", calls[1].Body);
    }

    [Fact]
    public async Task ExistingRecord_IsRemovedThenReAdded()
    {
        var calls = new List<(string Path, string Body)>();
        var provider = new NfsnProvider(
            Routed("[{\"name\":\"home\",\"type\":\"A\",\"data\":\"9.9.9.9\",\"ttl\":300}]", calls),
            NullLogger<NfsnProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, calls.Count);
        Assert.Equal("/dns/example.com/removeRR", calls[1].Path);
        Assert.Equal("name=home&type=A&data=9.9.9.9", calls[1].Body);
        Assert.Equal("/dns/example.com/addRR", calls[2].Path);
        Assert.Contains("data=1.2.3.4", calls[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRejected_IsFailure()
    {
        var calls = new List<(string Path, string Body)>();
        var provider = new NfsnProvider(
            Routed("[]", calls, (HttpStatusCode.Unauthorized, "{\"error\":\"Invalid authentication.\",\"debug\":\"The authentication hash does not match.\"}")),
            NullLogger<NfsnProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid authentication.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_CarryTheNfsnAuthenticationHeader()
    {
        var authHeaders = new List<string>();
        var factory = StubHttp.Factory(req =>
        {
            if (req.Headers.TryGetValues("X-NFSN-Authentication", out var values))
            {
                authHeaders.Add(values.Single());
            }

            return (HttpStatusCode.OK, req.RequestUri!.AbsolutePath.EndsWith("/listRRs", StringComparison.Ordinal) ? "[]" : string.Empty);
        });
        var provider = new NfsnProvider(factory, NullLogger<NfsnProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), V4, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, authHeaders.Count);
        foreach (var header in authHeaders)
        {
            // login;unix-timestamp;16-char-salt;sha1-hex — the hash itself is not recomputed here.
            Assert.Matches(new Regex("^member;\\d+;[A-Za-z0-9]{16};[0-9a-f]{40}$"), header);
        }
    }

    [Fact]
    public async Task HostOutsideZone_FailsWithoutNetwork()
    {
        var provider = new NfsnProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<NfsnProvider>.Instance);
        var record = Record();
        record.Hostname = "home.other.net";

        var result = await provider.UpdateAsync(record, V4, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Ipv6Only_FailsBecauseNfsnOnlyManagesARecords()
    {
        var provider = new NfsnProvider(
            StubHttp.Factory(_ => throw new Xunit.Sdk.XunitException("network")),
            NullLogger<NfsnProvider>.Instance);
        var record = Record();
        record.UpdateIPv4 = false;
        record.UpdateIPv6 = true;

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv6 = "2001:db8::1" }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
