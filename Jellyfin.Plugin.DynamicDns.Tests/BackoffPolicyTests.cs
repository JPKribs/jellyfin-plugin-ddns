using System;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the failure backoff state machine. A record pauses after the threshold of consecutive failures
/// and resumes once the window passes, and a success clears the count immediately.
/// </summary>
public class BackoffPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void BelowThreshold_DoesNotBackOff()
    {
        var record = new DNSRecord();
        BackoffPolicy.ApplyAttempt(record, success: false, threshold: 3, Window, Now);
        BackoffPolicy.ApplyAttempt(record, success: false, threshold: 3, Window, Now);

        Assert.Equal(2, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(BackoffPolicy.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void AtThreshold_StartsBackoff()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 3; i++)
        {
            BackoffPolicy.ApplyAttempt(record, success: false, threshold: 3, Window, Now);
        }

        Assert.Equal(3, record.ConsecutiveFailures);
        Assert.Equal(Now + Window, record.BackoffUntilUtc);
        Assert.True(BackoffPolicy.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void Backoff_ExpiresAfterWindow()
    {
        var record = new DNSRecord { ConsecutiveFailures = 3, BackoffUntilUtc = Now + Window };

        Assert.True(BackoffPolicy.IsBackingOff(record, 3, Now));
        Assert.True(BackoffPolicy.IsBackingOff(record, 3, Now + TimeSpan.FromHours(23)));
        Assert.False(BackoffPolicy.IsBackingOff(record, 3, Now + TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Success_ClearsFailuresAndBackoff()
    {
        var record = new DNSRecord { ConsecutiveFailures = 5, BackoffUntilUtc = Now + Window };
        BackoffPolicy.ApplyAttempt(record, success: true, threshold: 3, Window, Now);

        Assert.Equal(0, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(BackoffPolicy.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void Disabled_NeverBacksOff()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 10; i++)
        {
            BackoffPolicy.ApplyAttempt(record, success: false, threshold: 0, Window, Now);
        }

        Assert.Equal(10, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(BackoffPolicy.IsBackingOff(record, 0, Now + TimeSpan.FromDays(7)));
    }
}
