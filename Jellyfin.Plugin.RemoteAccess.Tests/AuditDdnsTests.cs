using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the dynamic DNS audit fixes: a partial success feeds backoff and only the failing family is
/// re-pushed, a failed lookup recreates a deleted record, a fresh push is trusted while resolvers still
/// cache the old answer, unticking a failing family lifts a pause, and on-screen provider text is plain.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class AuditDdnsTests
{
    private const string PublicV4 = "198.51.100.10";
    private const string PublicV6 = "2001:db8::10";

    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

    private static DNSRecord DualStack(string id = "pf") => new()
    {
        Id = id,
        Provider = DNSProviderKind.Ns1,
        Hostname = "home.example.com",
        Password = "t",
        UpdateIPv4 = true,
        UpdateIPv6 = true
    };

    private static DNSResolution Serving(params string[] ips)
        => DNSResolution.Resolved(ips.Select(IPAddress.Parse).ToList());

    private static string Text(Jellyfin.Database.Implementations.Entities.ActivityLog entry)
        => (entry.ShortOverview ?? string.Empty) + " " + (entry.Overview ?? string.Empty);

    // MARK: Partial success

    [Fact]
    public async Task PartialSuccess_RepeatedFamilyFailure_PausesTheRecord_AndRePushesOnlyThatFamily()
    {
        var sent = new List<string>();
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, (_, type, _) =>
            {
                lock (sent)
                {
                    sent.Add(type);
                }

                return Task.FromResult(type == "A" ? (true, "set") : (false, "AAAA rejected"));
            })
        });
        h.SetRecords(DualStack());

        // First run: nothing resolves yet, so both families are pushed and IPv4 lands.
        await h.RunAsync();
        Assert.Equal(new[] { "A", "AAAA" }, sent);
        var status = h.StatusOf("pf");
        Assert.Equal(PublicV4, status.LastIPv4);
        Assert.Equal(string.Empty, status.LastIPv6);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Equal(string.Empty, status.FailedIPv4);
        Assert.Equal(PublicV6, status.FailedIPv6);

        // DNS now serves the IPv4 that landed, so later runs push only the AAAA record.
        h.LookupAnswer = new[] { IPAddress.Parse(PublicV4) };
        sent.Clear();
        await h.RunAsync();
        await h.RunAsync();
        Assert.Equal(new[] { "AAAA", "AAAA" }, sent);

        status = h.StatusOf("pf");
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.True(status.BackoffUntilUtc!.Value > DateTime.UtcNow + TimeSpan.FromHours(23));
        Assert.Equal(PublicV4, status.LastIPv4);

        // Paused now: the same addresses do not contact the provider again.
        sent.Clear();
        var outcome = await h.RunAsync();
        Assert.Empty(sent);
        Assert.Equal("Backing off", Assert.Single(outcome.Records).Action);

        // The record was left on an old IPv6 address, so each failure is an error, then one pause warning.
        var failures = h.Activity.Where(e => e.Name.StartsWith("Dynamic DNS update failed", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, e => Assert.Equal(LogLevel.Error, e.LogSeverity));
        Assert.Single(h.Activity, e => e.Name.StartsWith("Dynamic DNS paused updates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialSuccess_WhenDnsAlreadyServesBoth_LogsAWarning()
    {
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, (_, type, _) => Task.FromResult(type == "A" ? (true, "set") : (false, "AAAA rejected")))
        });
        h.Plugin.MutateConfiguration(c =>
        {
            c.ForceUpdateHours = 1;
            return false;
        });
        h.SetRecords(DualStack());
        h.LookupAnswer = new[] { IPAddress.Parse(PublicV4), IPAddress.Parse(PublicV6) };

        // A forced refresh of a record DNS already serves, where one family is refused.
        await h.RunAsync();

        var entry = Assert.Single(h.Activity, e => e.Name.StartsWith("Dynamic DNS update failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.LogSeverity);
    }

    [Fact]
    public void ApplyOutcome_HeldFamily_IsNeitherPushedNorFailed()
    {
        var record = DualStack();
        record.LastIPv4 = "1.1.1.1";
        var result = new RecordOutcome { Success = false, Action = "Failed", IPv6Applied = false, IPv4Held = true };

        DNSUpdateService.ApplyOutcome(record, result, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }, backoffThreshold: 3, TimeSpan.FromHours(24));

        Assert.Equal("1.1.1.1", record.LastIPv4);
        Assert.Equal(string.Empty, record.FailedIPv4);
        Assert.Equal(PublicV6, record.FailedIPv6);
        Assert.Equal(1, record.ConsecutiveFailures);
    }

    [Fact]
    public void FamiliesNeedingPush_AfterPartialSuccess_SkipsTheLandedFamilyUnderSplitHorizon()
    {
        // A local resolver answers privately, so only the last push can be compared. IPv4 landed last time.
        var record = new DNSRecord
        {
            UpdateIPv4 = true,
            UpdateIPv6 = true,
            LastIPv4 = PublicV4,
            LastSuccess = false,
            FailedIPv6 = PublicV6
        };

        var (v4, v6) = UpdatePolicy.FamiliesNeedingPush(record, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }, Serving("10.0.0.2", "fd00::2"), Now);

        Assert.False(v4);
        Assert.True(v6);
    }

    [Fact]
    public void FamiliesNeedingPush_ProxiedPartialSuccess_SkipsTheLandedFamily()
    {
        var record = new DNSRecord
        {
            UpdateIPv4 = true,
            UpdateIPv6 = true,
            Proxied = true,
            LastIPv4 = PublicV4,
            LastSuccess = false,
            FailedIPv6 = PublicV6
        };

        var (v4, v6) = UpdatePolicy.FamiliesNeedingPush(record, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }, DNSResolution.NotChecked, Now);

        Assert.False(v4);
        Assert.True(v6);
    }

    // MARK: Failed lookups recreate deleted records

    [Fact]
    public void FailedLookup_AfterTheGraceWindow_PushesEvenWhenUnchanged()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = true, LastUpdateUtc = Now.AddHours(-2) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, DNSResolution.Failed, TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void EmptyAnswer_CountsAsAFailedLookup()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = true, LastUpdateUtc = Now.AddHours(-2) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving(), TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void PublicAnswerWithoutTheFamily_RecreatesThatFamily()
    {
        // The A record still resolves publicly but the AAAA record was deleted at the provider.
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = true, LastIPv4 = "1.2.3.4", LastIPv6 = "2001:db8::1", LastSuccess = true };

        var (v4, v6) = UpdatePolicy.FamiliesNeedingPush(record, new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" }, Serving("1.2.3.4"), Now);

        Assert.False(v4);
        Assert.True(v6);
    }

    [Fact]
    public void SplitHorizonAnswerWithoutTheFamily_StillComparesTheLastPush()
    {
        // A local resolver that only knows a private IPv4 says nothing about the public AAAA record.
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = true, LastIPv4 = "1.2.3.4", LastIPv6 = "2001:db8::1", LastSuccess = true };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" }, Serving("10.0.0.2"), TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void NameNotLookedUp_StillComparesTheLastPush()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = true, LastUpdateUtc = Now.AddDays(-3) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, DNSResolution.NotChecked, TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public async Task DeletedRecord_IsRecreatedOnTheNextRun()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", new RecordStatus
        {
            LastIPv4 = PublicV4,
            LastSuccess = true,
            LastAction = "Updated",
            LastUpdateUtc = DateTime.UtcNow - TimeSpan.FromHours(2)
        });

        // The lookup fails, as it does for a name deleted at the provider.
        var outcome = await h.RunAsync();

        Assert.Equal("Updated", Assert.Single(outcome.Records).Action);
        Assert.Equal(1, h.DdnsCalls);
    }

    [Fact]
    public async Task RecordWithNoResolvableName_IsNotRePushedEveryRun()
    {
        var provider = new FakeProvider(DNSProviderKind.DnsMadeEasy, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("ok")));
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "dme", Provider = DNSProviderKind.DnsMadeEasy, Hostname = "123456", Login = "u", Password = "t" });
        h.SeedStatus("dme", new RecordStatus
        {
            LastIPv4 = PublicV4,
            LastSuccess = true,
            LastUpdateUtc = DateTime.UtcNow - TimeSpan.FromDays(2)
        });

        var outcome = await h.RunAsync();

        Assert.Empty(h.Lookups);
        Assert.Empty(provider.Received);
        Assert.Equal("IP Unchanged", Assert.Single(outcome.Records).Action);
    }

    // MARK: Grace window after a push

    [Fact]
    public void FreshPush_StaleCachedAnswer_IsNotPushedAgain()
    {
        // The resolver still serves the old address a minute after the new one was pushed.
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "5.6.7.8", LastSuccess = true, LastUpdateUtc = Now.AddMinutes(-1) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, Serving("1.2.3.4"), TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void OldPush_StaleAnswer_IsPushedAgain()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "5.6.7.8", LastSuccess = true, LastUpdateUtc = Now.AddMinutes(-11) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, Serving("1.2.3.4"), TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void FailedPush_GetsNoGrace()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "5.6.7.8", LastSuccess = false, LastUpdateUtc = Now.AddMinutes(-1) };

        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, DNSResolution.Failed, TimeSpan.Zero, Now);

        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Theory]
    [InlineData(1, 600)]
    [InlineData(120, 120)]
    [InlineData(86400, 3600)]
    public void Grace_FollowsTheTtl_WithinLimits(int ttl, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UpdatePolicy.Grace(new DNSRecord { Ttl = ttl }));
    }

    // MARK: Backoff after a fix outside the target

    [Fact]
    public void AddressMoved_UntickingTheFailedFamily_LiftsThePause()
    {
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = false, LastIPv4 = PublicV4, FailedIPv4 = PublicV4, FailedIPv6 = PublicV6 };

        Assert.True(RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }));
    }

    [Fact]
    public void AddressMoved_PartialFailure_HoldsUntilAnAddressMoves()
    {
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = true, LastIPv4 = PublicV4, FailedIPv6 = PublicV6 };

        Assert.False(RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }));
        Assert.True(RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = "198.51.100.99", IPv6 = PublicV6 }));
        Assert.True(RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = PublicV4, IPv6 = "2001:db8::99" }));
    }

    [Fact]
    public async Task PausedRecord_IsRetried_OnceTheFailingFamilyIsUnticked()
    {
        var provider = new FakeProvider(DNSProviderKind.Cloudflare, (_, _, _) => Task.FromResult(DNSUpdateResult.Ok("ok")));
        using var h = new OrchestratorHarness(provider);
        h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Password = "t", UpdateIPv4 = true, UpdateIPv6 = false });
        h.SeedStatus("cf", new RecordStatus
        {
            LastIPv4 = PublicV4,
            ConsecutiveFailures = 3,
            BackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromHours(20),
            FailedIPv4 = PublicV4,
            FailedIPv6 = PublicV6
        });

        var outcome = await h.RunAsync();

        Assert.Equal("Updated", Assert.Single(outcome.Records).Action);
        Assert.Single(provider.Received);
        Assert.Null(h.StatusOf("cf").BackoffUntilUtc);
    }

    // MARK: Single request providers

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FreeMyIp_ReportsTheFamiliesItSent(bool v4, bool v6)
    {
        var provider = new FreeMyIpProvider(StubHttp.Always(HttpStatusCode.OK, "OK"), NullLogger<FreeMyIpProvider>.Instance);
        var record = new DNSRecord { Hostname = "home.freemyip.com", Password = "t", UpdateIPv4 = v4, UpdateIPv6 = v6 };

        var result = await provider.UpdateAsync(record, new DetectedIP { IPv4 = PublicV4, IPv6 = PublicV6 }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(v4 ? true : null, result.IPv4Applied);
        Assert.Equal(v6 ? true : null, result.IPv6Applied);
    }

    // MARK: On-screen text

    [Fact]
    public void ProviderHints_AreAtMostTwoPlainSentences()
    {
        foreach (var provider in AllProviders())
        {
            var hint = provider.Hint;
            Assert.DoesNotContain(';', hint);
            Assert.DoesNotContain('(', hint);
            Assert.DoesNotContain(')', hint);
            var sentences = Regex.Matches(hint, "[.!?](?=\\s|$)").Count;
            Assert.True(sentences <= 2, provider.Kind + " hint has " + sentences + " sentences: " + hint);
        }
    }

    [Theory]
    [InlineData(typeof(DynuProvider))]
    [InlineData(typeof(DynDns2Provider))]
    [InlineData(typeof(NoIpProvider))]
    [InlineData(typeof(InwxProvider))]
    [InlineData(typeof(HeNetProvider))]
    public void ProviderReplyMaps_HaveNoParenthesesOrSemicolons(Type providerType)
    {
        var field = providerType.GetField("Errors", BindingFlags.NonPublic | BindingFlags.Static);
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(field!.GetValue(null));
        foreach (var message in map.Values)
        {
            Assert.DoesNotContain(';', message);
            Assert.DoesNotContain('(', message);
            Assert.DoesNotContain(')', message);
        }
    }

    [Fact]
    public async Task PerFamilyMessage_IsPlainSentences()
    {
        using var h = new OrchestratorHarness(http => new IDNSProvider[]
        {
            new PerFamilyProvider(http, (_, _, _) => Task.FromResult((true, "set")))
        });
        h.SetRecords(DualStack());

        var outcome = await h.RunAsync();

        Assert.Equal("IPv4: set. IPv6: set.", Assert.Single(outcome.Records).Message);
    }

    [Fact]
    public async Task TemporaryPauseSummary_HasNoParentheses()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.ServiceUnavailable, "down");

        for (var i = 0; i < 3; i++)
        {
            await h.RunAsync();
        }

        var outcome = await h.RunAsync();
        var paused = Assert.Single(outcome.Records);
        Assert.Equal("Backing off", paused.Action);
        Assert.DoesNotContain('(', paused.Message);
        var entry = Assert.Single(h.Activity, e => e.Name.StartsWith("Dynamic DNS paused updates", StringComparison.Ordinal));
        Assert.DoesNotContain('(', Text(entry));
        Assert.DoesNotContain(';', Text(entry));
    }

    private static List<IDNSProvider> AllProviders()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(StubHttp.Always(HttpStatusCode.OK, string.Empty));
        foreach (var type in typeof(IDNSProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t)))
        {
            services.AddSingleton(typeof(IDNSProvider), type);
        }

        // The providers hold no resources, so the container can go once they are read.
        using var built = services.BuildServiceProvider();
        return built.GetServices<IDNSProvider>().ToList();
    }
}
