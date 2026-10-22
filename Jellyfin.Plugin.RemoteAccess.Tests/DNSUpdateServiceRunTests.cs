using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Runs the full <see cref="DNSUpdateService"/> pass against a live plugin instance and a real status
/// store: how failures feed backoff, when a pause is lifted, what the live DNS check resolves, the IPv6
/// clearing for single-address protocols, status pruning, the run lock, and cancellation.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class DNSUpdateServiceRunTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task TransientFailures_NeverEarnTheLongPause(HttpStatusCode code)
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (code, "down");

        for (var i = 0; i < 3; i++)
        {
            await h.RunAsync();
        }

        var status = h.StatusOf("rec1");
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(3, status.TransientFailures);

        // A short pause, not the 24 hour one.
        Assert.NotNull(status.BackoffUntilUtc);
        Assert.True(status.BackoffUntilUtc!.Value <= DateTime.UtcNow + TimeSpan.FromMinutes(16));
        Assert.Equal(3, h.DdnsCalls);
    }

    [Fact]
    public async Task NetworkErrors_CountAsTransient_UntilSustained()
    {
        // Status zero (the request never completed) comes from a server override nothing answers. It also
        // covers misconfiguration (a mistyped server, a host that no longer resolves), so a short run is
        // temporary but a sustained one takes the long window rather than retrying every two hours forever.
        using var h = new OrchestratorHarness();
        var record = OrchestratorHarness.DynDnsRecord();
        record.Server = "https://down.test";
        h.SetRecords(record);

        await h.RunAsync();

        var status = h.StatusOf("rec1");
        Assert.False(status.LastSuccess);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(1, status.TransientFailures);

        // The IP moves each run so every pause is retried through; after three pauses at the cap the
        // ninth failure takes the long window.
        for (var i = 0; i < 8; i++)
        {
            h.PublicIPv4 = "198.51.100." + (60 + i);
            await h.RunAsync();
        }

        status = h.StatusOf("rec1");
        Assert.Equal(9, status.TransientFailures);
        Assert.True(status.BackoffUntilUtc!.Value > DateTime.UtcNow + TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task TransientPause_EscalatesButStaysShort()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.BadGateway, "bad gateway");

        // Each run after the pause starts is let through by moving the public IP, so the escalation is seen.
        for (var i = 0; i < 8; i++)
        {
            h.PublicIPv4 = "198.51.100." + (20 + i);
            await h.RunAsync();
        }

        var status = h.StatusOf("rec1");
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(8, status.TransientFailures);
        Assert.True(status.BackoffUntilUtc!.Value <= DateTime.UtcNow + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        Assert.True(status.BackoffUntilUtc!.Value >= DateTime.UtcNow + TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.BadRequest, "bad request")]
    [InlineData(HttpStatusCode.OK, "badauth")]
    public async Task DefinitiveFailures_KeepTheLongPause(HttpStatusCode code, string body)
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (code, body);

        for (var i = 0; i < 3; i++)
        {
            await h.RunAsync();
        }

        var status = h.StatusOf("rec1");
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.True(status.BackoffUntilUtc!.Value > DateTime.UtcNow + TimeSpan.FromHours(23));
        Assert.Equal("198.51.100.10", status.FailedIPv4);

        // Paused: the next run with the same address does not contact the provider.
        var outcome = await h.RunAsync();
        Assert.Equal(3, h.DdnsCalls);
        Assert.Equal("Backing off", Assert.Single(outcome.Records).Action);
    }

    [Fact]
    public async Task PausedRecord_IsRetriedWhenThePublicIPChanges()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", new RecordStatus
        {
            LastIPv4 = "198.51.100.10",
            LastCheckedUtc = DateTime.UtcNow,
            ConsecutiveFailures = 3,
            BackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromHours(20),
            FailedIPv4 = "198.51.100.10"
        });

        // Same address: still paused.
        var paused = await h.RunAsync();
        Assert.Equal("Backing off", Assert.Single(paused.Records).Action);
        Assert.Equal(0, h.DdnsCalls);

        // The address moved: the pause no longer holds the new IP back.
        h.PublicIPv4 = "198.51.100.77";
        var outcome = await h.RunAsync();

        var result = Assert.Single(outcome.Records);
        Assert.Equal("Updated", result.Action);
        Assert.Equal(1, h.DdnsCalls);
        var status = h.StatusOf("rec1");
        Assert.Equal("198.51.100.77", status.LastIPv4);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.BackoffUntilUtc);
        Assert.Equal(string.Empty, status.FailedIPv4);
    }

    [Fact]
    public async Task PausedRecord_RetriedOnIPChange_FailingAgain_PausesAtTheNewAddress()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.OK, "badauth");
        h.SeedStatus("rec1", new RecordStatus
        {
            ConsecutiveFailures = 3,
            BackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromHours(20),
            FailedIPv4 = "198.51.100.1"
        });

        await h.RunAsync();
        await h.RunAsync();

        // One retry for the new address, then paused again: credentials that are wrong stay wrong.
        Assert.Equal(1, h.DdnsCalls);
        var status = h.StatusOf("rec1");
        Assert.Equal(4, status.ConsecutiveFailures);
        Assert.Equal("198.51.100.10", status.FailedIPv4);
    }

    [Fact]
    public async Task LegacyPausedStatus_WithoutFailedAddress_GetsOneRetry()
    {
        // Status written before the failed address was tracked: a 24 hour pause from three timeouts.
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", new RecordStatus
        {
            ConsecutiveFailures = 3,
            BackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromHours(20)
        });

        var outcome = await h.RunAsync();

        Assert.Equal("Updated", Assert.Single(outcome.Records).Action);
        Assert.Equal(1, h.DdnsCalls);
    }

    [Fact]
    public async Task ProviderReply_IsSanitizedBeforeStatusAndActivityLog()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.OK, "weird\u001b[31m" + new string('x', 2000));

        var outcome = await h.RunAsync();

        var message = Assert.Single(outcome.Records).Message;
        Assert.DoesNotContain(message, char.IsControl);
        Assert.True(message.Length <= 303, "message was " + message.Length + " characters");
        Assert.Equal(message, h.StatusOf("rec1").LastStatus);
    }

    [Fact]
    public async Task ProviderReply_EchoingTheCredentials_NeverReachesTheStatus()
    {
        using var h = new OrchestratorHarness();
        var record = OrchestratorHarness.DynDnsRecord();
        record.Login = "media-user";
        record.Password = "Sup3r-Secret-Pa55";
        h.SetRecords(record);
        h.DdnsReply = (HttpStatusCode.OK, "badauth for media-user with Sup3r-Secret-Pa55 token=abc123");

        var outcome = await h.RunAsync();

        var message = Assert.Single(outcome.Records).Message;
        Assert.DoesNotContain("Sup3r-Secret-Pa55", message, StringComparison.Ordinal);
        Assert.DoesNotContain("media-user", message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3r-Secret-Pa55", h.StatusOf("rec1").LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveDnsCheck_ResolvesTheZoneCompletedName()
    {
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("ok")));
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home", Zone = "example.com", Password = "t" });
        h.LookupAnswer = new[] { IPAddress.Parse("198.51.100.10") };

        var outcome = await h.RunAsync();

        Assert.Equal(new[] { "home.example.com" }, h.Lookups);

        // DNS already serves the detected address, so nothing is pushed.
        Assert.Equal("IP Unchanged", Assert.Single(outcome.Records).Action);
        Assert.Empty(provider.Received);
    }

    [Theory]
    [InlineData(DNSProviderKind.DnsMadeEasy, "123456")]
    [InlineData(DNSProviderKind.Sitelutions, "987654")]
    [InlineData(DNSProviderKind.DuckDns, "one,two")]
    public async Task LiveDnsCheck_IsSkippedWhenNoNameCanBeBuilt(DNSProviderKind kind, string hostname)
    {
        var provider = new FakeProvider(kind, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("ok")));
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "r", Provider = kind, Hostname = hostname, Password = "t" });
        h.LookupAnswer = new[] { IPAddress.Parse("198.51.100.10") };

        var outcome = await h.RunAsync();

        Assert.Empty(h.Lookups);
        Assert.Equal("Updated", Assert.Single(outcome.Records).Action);
    }

    [Fact]
    public async Task LiveDnsCheck_DuckDnsLabel_ResolvesTheDuckDnsName()
    {
        var provider = new FakeProvider(DNSProviderKind.DuckDns, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("OK")));
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "d", Provider = DNSProviderKind.DuckDns, Hostname = "myhome", Password = "t" });

        await h.RunAsync();

        Assert.Equal(new[] { "myhome.duckdns.org" }, h.Lookups);
    }

    [Fact]
    public async Task SingleAddressProvider_HasIPv6ClearedForTheRun()
    {
        var provider = new FakeProvider(DNSProviderKind.Ovh, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("good")), supportsIPv6: false);
        using var h = new OrchestratorHarness(provider);
        var record = new DNSRecord { Id = "ovh", Provider = DNSProviderKind.Ovh, Hostname = "home.example.com", Login = "u", Password = "p", UpdateIPv4 = true, UpdateIPv6 = true };
        h.SetRecords(record);

        var outcome = await h.RunAsync();

        // IPv6 is neither detected nor pushed, and the stored record keeps its setting.
        Assert.Null(outcome.DetectedIPv6);
        var sent = Assert.Single(provider.Received);
        Assert.False(sent.UpdateIPv6);
        Assert.Equal(string.Empty, h.StatusOf("ovh").LastIPv6);
        Assert.True(h.Plugin.ReadConfiguration(c => c.Records[0].UpdateIPv6));
    }

    [Fact]
    public async Task Status_ForRemovedRecords_IsPruned_ButKeptForDisabledOnes()
    {
        using var h = new OrchestratorHarness();
        var disabled = OrchestratorHarness.DynDnsRecord("off");
        disabled.Enabled = false;
        h.SetRecords(OrchestratorHarness.DynDnsRecord(), disabled);
        h.SeedStatus("gone", new RecordStatus { LastIPv4 = "198.51.100.1" });
        h.SeedStatus("off", new RecordStatus { LastIPv4 = "198.51.100.2" });

        await h.RunAsync();

        var records = h.Status.Load().Records;
        Assert.False(records.ContainsKey("gone"));
        Assert.Equal("198.51.100.2", records["off"].LastIPv4);
        Assert.True(records.ContainsKey("rec1"));
    }

    [Fact]
    public async Task RecordRetargetedMidRun_DoesNotInheritTheRunsStatus()
    {
        OrchestratorHarness? h = null;
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, (_, _, _) =>
        {
            // The administrator points the record at another host while its push is in flight.
            h!.Plugin.MutateConfiguration(c =>
            {
                c.Records[0].Hostname = "other.example.com";
                return false;
            });
            return Task.FromResult(DNSUpdateResult.Ok("ok"));
        });
        using (h = new OrchestratorHarness(provider))
        {
            h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Password = "t" });

            await h.RunAsync();

            Assert.False(h.Status.Load().Records.ContainsKey("cf"));
        }
    }

    [Fact]
    public async Task RunLock_SecondConcurrentRunIsSkipped()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await gate.Task;
            return DNSUpdateResult.Ok("ok");
        });
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Password = "t" });

        var first = h.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await h.RunAsync();
        Assert.Contains(second.Warnings, w => w.Contains("already running", StringComparison.Ordinal));
        Assert.Empty(second.Records);

        gate.SetResult(true);
        var done = await first;
        Assert.Equal("Updated", Assert.Single(done.Records).Action);

        // The lock is released, so a later run proceeds.
        var third = await h.RunAsync();
        Assert.DoesNotContain(third.Warnings, w => w.Contains("already running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_MidRun_StillPersistsPushesThatLanded()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, (record, _, _) =>
        {
            if (record.Id == "second")
            {
                // The run is cancelled while the second record is being pushed.
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return Task.FromResult(DNSUpdateResult.Ok("ok"));
        });
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(
            new DNSRecord { Id = "first", Provider = DNSProviderKind.Cloudflare, Hostname = "a.example.com", Zone = "example.com", Password = "t" },
            new DNSRecord { Id = "second", Provider = DNSProviderKind.Cloudflare, Hostname = "b.example.com", Zone = "example.com", Password = "t" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cts.Token));

        var records = h.Status.Load().Records;
        Assert.Equal("198.51.100.10", records["first"].LastIPv4);
        Assert.True(records["first"].LastSuccess);
        Assert.False(records.ContainsKey("second"));

        // The landed push is logged too.
        Assert.Contains(h.Activity, e => e.Name.Contains("updated a.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_BeforeTheRun_Throws_WithoutTouchingStatus()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cts.Token));
        Assert.Empty(h.Status.Load().Records);
        Assert.Equal(0, h.DdnsCalls);
    }

    private static DNSRecord DualStack(string id = "pf") => new()
    {
        Id = id,
        Provider = DNSProviderKind.Ns1,
        Hostname = "home.example.com",
        Password = "t",
        UpdateIPv4 = true,
        UpdateIPv6 = true
    };

    [Fact]
    public async Task DefinitiveFamily_IsNotMaskedByALaterFamilysServerError()
    {
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, async (p, type, ct) =>
            {
                var r = await p.GetAsync(type == "A" ? "https://api.test/a" : "https://api.test/aaaa", ct);
                return (r.Ok, "HTTP " + r.Status);
            })
        });
        h.Hook = (req, _) => Task.FromResult(req.RequestUri!.Host != "api.test" ? null
            : Reply(req.RequestUri.AbsolutePath == "/a" ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable));
        h.SetRecords(DualStack());

        await h.RunAsync();

        var status = h.StatusOf("pf");
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Equal(0, status.TransientFailures);
    }

    [Fact]
    public async Task OnlyAAAATimesOut_ThrowingLikeBase1019_KeepsTheIPv4Push()
    {
        // The per request timeout spans both families. The AAAA push waits on the request token and its
        // cancellation escapes as an exception, as JpkHttp does from 2026.10.19 on.
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, async (_, type, ct) =>
            {
                if (type == "AAAA")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return (true, "ok");
            })
        });
        h.Plugin.MutateConfiguration(c =>
        {
            c.RequestTimeoutSeconds = 1;
            return false;
        });
        h.SetRecords(DualStack());

        var outcome = await h.RunAsync();

        var result = Assert.Single(outcome.Records);
        Assert.Equal("Failed", result.Action);
        Assert.True(result.IPv4Applied);
        Assert.False(result.IPv6Applied);
        var status = h.StatusOf("pf");
        Assert.Equal("198.51.100.10", status.LastIPv4);
        Assert.Equal(string.Empty, status.LastIPv6);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task OnlyAAAATimesOut_SwallowedLikeBase1016_KeepsTheIPv4Push()
    {
        // The AAAA request hangs until the request timeout cancels it; JpkHttp 2026.10.16 turns that into
        // status zero.
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, async (p, type, ct) =>
            {
                var r = await p.GetAsync(type == "A" ? "https://api.test/a" : "https://api.test/aaaa", ct);
                return (r.Ok, "HTTP " + r.Status);
            })
        });
        h.Hook = async (req, ct) =>
        {
            if (req.RequestUri!.Host != "api.test")
            {
                return null;
            }

            if (req.RequestUri.AbsolutePath == "/aaaa")
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        h.Plugin.MutateConfiguration(c =>
        {
            c.RequestTimeoutSeconds = 1;
            return false;
        });
        h.SetRecords(DualStack());

        var outcome = await h.RunAsync();

        var result = Assert.Single(outcome.Records);
        Assert.True(result.IPv4Applied);
        Assert.False(result.IPv6Applied);
        Assert.Equal("198.51.100.10", h.StatusOf("pf").LastIPv4);
    }

    [Fact]
    public async Task RunCancellation_DuringAFamily_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, (_, type, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult((true, "ok"));
            })
        });
        h.SetRecords(DualStack());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cts.Token));
    }

    [Fact]
    public async Task StatusResetMidRun_ForARecordTheRunDidNotTouch_IsNotRestored()
    {
        OrchestratorHarness? h = null;
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, (_, _, _) =>
        {
            // The dashboard resets the disabled record's status while this run is in flight.
            h!.Status.RemoveRecords(new[] { "off" });
            return Task.FromResult(DNSUpdateResult.Ok("ok"));
        });
        using (h = new OrchestratorHarness(provider))
        {
            var off = OrchestratorHarness.DynDnsRecord("off");
            off.Enabled = false;
            h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Password = "t" }, off);
            h.SeedStatus("off", new RecordStatus { LastIPv4 = "198.51.100.2", ConsecutiveFailures = 3 });

            await h.RunAsync();

            var records = h.Status.Load().Records;
            Assert.False(records.ContainsKey("off"));
            Assert.True(records.ContainsKey("cf"));
        }
    }

    [Fact]
    public async Task NewPause_AfterAnExpiredOne_IsLogged()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.ServiceUnavailable, "down");

        // A past short pause that has expired, two temporary failures into a new streak.
        h.SeedStatus("rec1", new RecordStatus { TransientFailures = 2, BackoffUntilUtc = DateTime.UtcNow - TimeSpan.FromHours(1), FailedIPv4 = "198.51.100.10" });

        await h.RunAsync();

        Assert.NotNull(h.StatusOf("rec1").BackoffUntilUtc);
        Assert.Contains(h.Activity, e => e.Name.StartsWith("Dynamic DNS paused updates", StringComparison.Ordinal));
    }

    // The response is handed to the HTTP client, which disposes it.
    private static HttpResponseMessage? Reply(HttpStatusCode status) => new(status);
}
