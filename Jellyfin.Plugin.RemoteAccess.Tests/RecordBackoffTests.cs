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
public class RecordBackoffTests
{
    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void BelowThreshold_DoesNotBackOff()
    {
        var record = new DNSRecord();
        RecordBackoff.ApplyAttempt(record, success: false, threshold: 3, Window, Now);
        RecordBackoff.ApplyAttempt(record, success: false, threshold: 3, Window, Now);

        Assert.Equal(2, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(RecordBackoff.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void AtThreshold_StartsBackoff()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 3; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, threshold: 3, Window, Now);
        }

        Assert.Equal(3, record.ConsecutiveFailures);
        Assert.Equal(Now + Window, record.BackoffUntilUtc);
        Assert.True(RecordBackoff.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void Backoff_ExpiresAfterWindow()
    {
        var record = new DNSRecord { ConsecutiveFailures = 3, BackoffUntilUtc = Now + Window };

        Assert.True(RecordBackoff.IsBackingOff(record, 3, Now));
        Assert.True(RecordBackoff.IsBackingOff(record, 3, Now + TimeSpan.FromHours(23)));
        Assert.False(RecordBackoff.IsBackingOff(record, 3, Now + TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Success_ClearsFailuresAndBackoff()
    {
        var record = new DNSRecord { ConsecutiveFailures = 5, BackoffUntilUtc = Now + Window };
        RecordBackoff.ApplyAttempt(record, success: true, threshold: 3, Window, Now);

        Assert.Equal(0, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(RecordBackoff.IsBackingOff(record, 3, Now));
    }

    [Fact]
    public void Disabled_NeverBacksOff()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 10; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, threshold: 0, Window, Now);
        }

        Assert.Equal(10, record.ConsecutiveFailures);
        Assert.Null(record.BackoffUntilUtc);
        Assert.False(RecordBackoff.IsBackingOff(record, 0, Now + TimeSpan.FromDays(7)));
    }
}

/// <summary>
/// Covers the temporary failure path: it never touches the definitive count, earns a short escalating
/// pause capped well under the long window, never shortens a pause already in force, and a definitive
/// failure or success resets it. Also covers when a paused record's address counts as moved.
/// </summary>
public class RecordBackoffTransientTests
{
    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void TransientFailures_DoNotCountTowardTheLongPause()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 5; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 3, Window, Now);
        }

        Assert.Equal(0, record.ConsecutiveFailures);
        Assert.Equal(5, record.TransientFailures);
        Assert.True(record.BackoffUntilUtc <= Now + TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 15)]
    [InlineData(4, 30)]
    [InlineData(5, 60)]
    [InlineData(6, 120)]
    [InlineData(8, 120)]
    public void TransientPause_StartsAtFifteenMinutesAndDoublesToTwoHours(int failures, int expectedMinutes)
    {
        var record = new DNSRecord();
        for (var i = 0; i < failures; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 3, Window, Now);
        }

        if (expectedMinutes == 0)
        {
            Assert.Null(record.BackoffUntilUtc);
        }
        else
        {
            Assert.Equal(Now + TimeSpan.FromMinutes(expectedMinutes), record.BackoffUntilUtc);
        }
    }

    [Fact]
    public void SustainedTransientFailures_EarnTheLongWindow()
    {
        // Three short pauses at the two hour cap (failures 6, 7, 8), then the long window. A failure that
        // never clears, such as a provider host that no longer resolves, is not retried every two hours forever.
        var record = new DNSRecord();
        for (var i = 0; i < 9; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 3, Window, Now);
        }

        Assert.Equal(Now + Window, record.BackoffUntilUtc);
        Assert.Equal(0, record.ConsecutiveFailures);
    }

    [Fact]
    public void TransientPause_NeverOutlastsAShortConfiguredWindow()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 10; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 3, TimeSpan.FromHours(1), Now);
        }

        Assert.Equal(Now + TimeSpan.FromHours(1), record.BackoffUntilUtc);
    }

    [Fact]
    public void TransientFailure_NeverShortensALongPauseInForce()
    {
        var record = new DNSRecord { ConsecutiveFailures = 3, BackoffUntilUtc = Now + Window, TransientFailures = 5 };
        RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 3, Window, Now);

        Assert.Equal(Now + Window, record.BackoffUntilUtc);
        Assert.Equal(3, record.ConsecutiveFailures);
    }

    [Fact]
    public void DefinitiveFailure_ResetsTheTransientRun()
    {
        var record = new DNSRecord { TransientFailures = 4 };
        RecordBackoff.ApplyAttempt(record, success: false, transient: false, threshold: 3, Window, Now);

        Assert.Equal(0, record.TransientFailures);
        Assert.Equal(1, record.ConsecutiveFailures);
    }

    [Fact]
    public void Success_ClearsTransientState()
    {
        var record = new DNSRecord { TransientFailures = 4, BackoffUntilUtc = Now + TimeSpan.FromHours(1) };
        RecordBackoff.ApplyAttempt(record, success: true, transient: false, threshold: 3, Window, Now);

        Assert.Equal(0, record.TransientFailures);
        Assert.Null(record.BackoffUntilUtc);
    }

    [Fact]
    public void Disabled_CountsTransientFailuresButNeverPauses()
    {
        var record = new DNSRecord();
        for (var i = 0; i < 10; i++)
        {
            RecordBackoff.ApplyAttempt(record, success: false, transient: true, threshold: 0, Window, Now);
        }

        Assert.Equal(10, record.TransientFailures);
        Assert.Null(record.BackoffUntilUtc);
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", false)]
    [InlineData("1.2.3.4", "5.6.7.8", true)]
    [InlineData("", "1.2.3.4", true)]
    public void AddressMoved_ComparesWantedFamiliesToTheLastFailedAddress(string failed, string detected, bool moved)
    {
        var record = new DNSRecord { UpdateIPv4 = true, FailedIPv4 = failed };
        Assert.Equal(moved, RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = detected }));
    }

    [Fact]
    public void AddressMoved_IgnoresFamiliesTheRecordDoesNotUpdateOrThatWereNotDetected()
    {
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = false, FailedIPv4 = "1.2.3.4" };
        Assert.False(RecordBackoff.AddressMoved(record, new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::9" }));
        Assert.False(RecordBackoff.AddressMoved(record, new DetectedIP()));
    }
}
