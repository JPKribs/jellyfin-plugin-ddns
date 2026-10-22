using System;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Binds a <see cref="DNSRecord"/>'s persisted failure state to the shared <see cref="BackoffPolicy"/>.
/// The failure counting and pause math live in the base policy; this only maps the record's state fields
/// onto it and honours the ddns convention that a threshold of zero disables backoff (the base policy
/// requires a threshold of at least one).
/// <para>
/// Failures come in two kinds. A definitive failure (bad credentials, a rejected request, an error the
/// provider reports in its reply) means the record is misconfigured, so it counts toward the long fixed
/// pause. A temporary failure (a timeout, a network error, a rate limit, or a provider server error) says
/// nothing about the record, so it never counts toward the long pause and only earns a short escalating
/// one. Either pause is lifted early when the detected address moves, since a new address is worth trying.
/// </para>
/// </summary>
public static class RecordBackoff
{
    /// <summary>The first short pause after the threshold of temporary failures.</summary>
    internal static readonly TimeSpan TransientBaseDelay = TimeSpan.FromMinutes(15);

    /// <summary>The longest short pause temporary failures can escalate to.</summary>
    internal static readonly TimeSpan TransientMaxDelay = TimeSpan.FromHours(2);

    /// <summary>
    /// How many short pauses at the cap a record may take before its temporary failures earn the long
    /// window. A failure that never clears (a provider host that no longer resolves, a certificate that
    /// does not match, a mistyped server override) looks temporary on every attempt, so a sustained run
    /// is treated like a definitive failure rather than retried every two hours forever.
    /// </summary>
    internal const int TransientCapHitsBeforeLongPause = 3;

    /// <summary>
    /// Returns whether a record is currently paused by the backoff policy.
    /// </summary>
    /// <param name="record">The record to evaluate.</param>
    /// <param name="threshold">The consecutive failure count that triggers a pause. Zero disables backoff.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>True when the record should be skipped without contacting the provider.</returns>
    public static bool IsBackingOff(DNSRecord record, int threshold, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);
        return threshold > 0 && new BackoffPolicy(threshold).IsBackingOff(ToState(record), utcNow);
    }

    /// <summary>
    /// Returns whether a wanted address moved away from the one the last failed attempt tried to push. A
    /// paused record whose address moved is retried at once, so a pause never holds back a new public IP.
    /// A record paused before the failed addresses were tracked has none stored, which counts as moved and
    /// earns it one retry.
    /// <para>
    /// After a partial success only the family that failed keeps a failed address. The family that landed
    /// counts as moved once its detected address leaves the one last pushed. A family the failed attempt
    /// carried that the record no longer updates, such as IPv6 unticked after its pushes kept failing, also
    /// counts as a change, so the fix is tried at once instead of after the pause.
    /// </para>
    /// </summary>
    /// <param name="record">The record to evaluate.</param>
    /// <param name="ip">The freshly detected public addresses.</param>
    /// <returns>True when a wanted family's detected address differs from the last failed one.</returns>
    public static bool AddressMoved(DNSRecord record, DetectedIP ip)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        var wantsV4 = record.WantsIPv4(ip);
        var wantsV6 = record.WantsIPv6(ip);
        var failedV4 = !string.IsNullOrEmpty(record.FailedIPv4);
        var failedV6 = !string.IsNullOrEmpty(record.FailedIPv6);

        // The record no longer updates a family the failed attempt carried, so what it sends has changed.
        if ((failedV4 && !record.UpdateIPv4) || (failedV6 && !record.UpdateIPv6))
        {
            return true;
        }

        var trackedV4 = wantsV4 && failedV4;
        var trackedV6 = wantsV6 && failedV6;
        if (!trackedV4 && !trackedV6)
        {
            // Nothing stored for a wanted family, as for a pause written before failed addresses were tracked.
            return wantsV4 || wantsV6;
        }

        return Moved(wantsV4, trackedV4, ip.IPv4, record.FailedIPv4, record.LastIPv4)
            || Moved(wantsV6, trackedV6, ip.IPv6, record.FailedIPv6, record.LastIPv6);
    }

    // A family that failed moved once the detected address leaves the failed one. A wanted family with no
    // failed address landed on the last attempt, so it moved once the detected address leaves the pushed one.
    private static bool Moved(bool wanted, bool tracked, string? detected, string failed, string lastPushed)
    {
        if (!wanted)
        {
            return false;
        }

        return !string.Equals(detected, tracked ? failed : lastPushed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Updates a record's failure count and backoff time after a real attempt that contacted the provider,
    /// treating a failure as definitive.
    /// </summary>
    /// <param name="record">The record to update.</param>
    /// <param name="success">Whether the attempt succeeded.</param>
    /// <param name="threshold">The consecutive failure count that triggers a pause. Zero disables backoff.</param>
    /// <param name="window">How long the pause lasts.</param>
    /// <param name="utcNow">The current UTC time.</param>
    public static void ApplyAttempt(DNSRecord record, bool success, int threshold, TimeSpan window, DateTime utcNow)
        => ApplyAttempt(record, success, transient: false, threshold, window, utcNow);

    /// <summary>
    /// Updates a record's failure state after a real attempt that contacted the provider. A success clears
    /// everything. A definitive failure increments the failure count and starts the long fixed pause once
    /// the threshold is met. A temporary failure leaves the definitive count alone and, once the threshold
    /// of temporary failures in a row is met, starts a short pause that doubles per further failure.
    /// </summary>
    /// <param name="record">The record to update.</param>
    /// <param name="success">Whether the attempt succeeded.</param>
    /// <param name="transient">Whether a failure was temporary rather than a sign of misconfiguration.</param>
    /// <param name="threshold">The consecutive failure count that triggers a pause. Zero disables backoff.</param>
    /// <param name="window">How long the long pause lasts.</param>
    /// <param name="utcNow">The current UTC time.</param>
    public static void ApplyAttempt(DNSRecord record, bool success, bool transient, int threshold, TimeSpan window, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (success)
        {
            FromState(record, BackoffState.Initial);
            record.TransientFailures = 0;
            return;
        }

        if (transient)
        {
            ApplyTransientFailure(record, threshold, window, utcNow);
            return;
        }

        // A definitive failure ends any run of temporary ones.
        record.TransientFailures = 0;

        if (threshold <= 0)
        {
            // Backoff disabled: keep counting failures but never pause.
            record.ConsecutiveFailures++;
            return;
        }

        // Equal base and maximum delay make the base policy's window fixed rather than escalating.
        var policy = new BackoffPolicy(threshold, window, window);
        FromState(record, policy.RecordFailure(ToState(record), utcNow));
    }

    private static void ApplyTransientFailure(DNSRecord record, int threshold, TimeSpan window, DateTime utcNow)
    {
        var previous = record.TransientFailures;
        record.TransientFailures = previous + 1;
        if (threshold <= 0)
        {
            return;
        }

        // The short pause never outlasts the long one the administrator configured.
        var max = window < TransientMaxDelay ? window : TransientMaxDelay;
        var baseDelay = TransientBaseDelay < max ? TransientBaseDelay : max;
        var policy = new BackoffPolicy(threshold, baseDelay, max);
        var until = policy.RecordFailure(new BackoffState(previous, null), utcNow).BackoffUntilUtc;

        // A sustained run of temporary failures that has sat at the cap long enough takes the long window.
        if (IsSustained(record.TransientFailures, threshold, baseDelay, max))
        {
            until = utcNow + window;
        }

        // Never shorten a pause already in force (a long pause the record was retried through early).
        if (until is not null && (record.BackoffUntilUtc is null || until > record.BackoffUntilUtc))
        {
            record.BackoffUntilUtc = until;
        }
    }

    /// <summary>
    /// Returns whether a run of temporary failures is long enough to earn the long pause: the short pause
    /// has already reached its cap the configured number of times.
    /// </summary>
    /// <param name="failures">The consecutive temporary failures, including the one just recorded.</param>
    /// <param name="threshold">The failure count that starts the short pause.</param>
    /// <param name="baseDelay">The first short pause.</param>
    /// <param name="max">The short pause cap.</param>
    /// <returns>True when the long window applies.</returns>
    internal static bool IsSustained(int failures, int threshold, TimeSpan baseDelay, TimeSpan max)
    {
        // The step at which the doubling first reaches the cap (0 at the threshold).
        var capStep = 0;
        for (var delay = baseDelay; delay < max && capStep < 62; delay += delay)
        {
            capStep++;
        }

        var capHits = failures - (threshold + capStep) + 1;
        return capHits > TransientCapHitsBeforeLongPause;
    }

    private static BackoffState ToState(DNSRecord record)
        => new(record.ConsecutiveFailures, record.BackoffUntilUtc);

    private static void FromState(DNSRecord record, BackoffState state)
    {
        record.ConsecutiveFailures = state.ConsecutiveFailures;
        record.BackoffUntilUtc = state.BackoffUntilUtc;
    }
}
