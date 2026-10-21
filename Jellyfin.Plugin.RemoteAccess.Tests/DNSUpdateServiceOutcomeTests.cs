using System;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the per-record bookkeeping applied after a push (<see cref="DNSUpdateService.ApplyOutcome"/>):
/// only addresses that actually landed are recorded per family, a partial success does not feed the
/// backoff counter, a full failure does, and skips leave the counters untouched.
/// </summary>
public class DNSUpdateServiceOutcomeTests
{
    private static readonly DetectedIP BothFamilies = new() { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" };
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        UpdateIPv4 = true,
        UpdateIPv6 = true
    };

    private static RecordOutcome Outcome(bool success, string action = "", bool skipped = false) => new()
    {
        Success = success,
        Action = action.Length > 0 ? action : (success ? "Updated" : "Failed"),
        Skipped = skipped,
        Message = "test"
    };

    [Fact]
    public void FullSuccess_RecordsBothAddressesAndTheUpdateTime()
    {
        var record = Record();
        var result = Outcome(success: true);
        result.IPv4Applied = true;
        result.IPv6Applied = true;

        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);

        Assert.Equal("1.2.3.4", record.LastIPv4);
        Assert.Equal("2001:db8::1", record.LastIPv6);
        Assert.NotNull(record.LastUpdateUtc);
        Assert.Equal(0, record.ConsecutiveFailures);
    }

    [Fact]
    public void PartialSuccess_RecordsOnlyTheLandedFamily_AndDoesNotFeedBackoff()
    {
        var record = Record();
        var result = Outcome(success: false);
        result.IPv4Applied = true;
        result.IPv6Applied = false;

        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);

        Assert.Equal("1.2.3.4", record.LastIPv4);
        Assert.Equal(string.Empty, record.LastIPv6);
        Assert.Null(record.LastUpdateUtc);
        Assert.Equal(0, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(record.LastSuccess);
    }

    [Fact]
    public void FullFailure_CountsTowardBackoffAndPausesAtTheThreshold()
    {
        var record = Record();
        var result = Outcome(success: false);
        result.IPv4Applied = false;
        result.IPv6Applied = false;

        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);
        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);
        Assert.Null(record.BackoffUntilUtc);
        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);

        Assert.Equal(3, record.ConsecutiveFailures);
        Assert.NotNull(record.BackoffUntilUtc);
        Assert.Equal(string.Empty, record.LastIPv4);
    }

    [Fact]
    public void ProviderWithoutPerFamilyFlags_FallsBackToOverallSuccess()
    {
        var record = Record();
        record.UpdateIPv6 = false;
        var result = Outcome(success: true);

        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);

        Assert.Equal("1.2.3.4", record.LastIPv4);
        Assert.Equal(string.Empty, record.LastIPv6);
        Assert.NotNull(record.LastUpdateUtc);
    }

    [Fact]
    public void Skip_LeavesFailureCountersUntouched()
    {
        var record = Record();
        record.ConsecutiveFailures = 2;
        var result = Outcome(success: true, action: "IP Unchanged", skipped: true);

        DNSUpdateService.ApplyOutcome(record, result, BothFamilies, backoffThreshold: 3, Window);

        Assert.Equal(2, record.ConsecutiveFailures);
        Assert.Equal("IP Unchanged", record.LastAction);
        Assert.Null(record.LastUpdateUtc);
    }
}
