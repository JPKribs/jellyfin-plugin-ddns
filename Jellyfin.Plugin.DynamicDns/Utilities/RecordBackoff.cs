using System;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Binds a <see cref="DNSRecord"/>'s persisted failure state to the shared <see cref="BackoffPolicy"/>.
/// The failure counting and pause math live in the base policy; this only maps the record's two state
/// fields onto it and honours the ddns convention that a threshold of zero disables backoff (the base
/// policy requires a threshold of at least one). The pause window is fixed rather than escalating, which
/// the base policy expresses as an equal base and maximum delay.
/// </summary>
public static class RecordBackoff
{
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
    /// Updates a record's failure count and backoff time after a real attempt that contacted the provider.
    /// A success clears the count, a failure increments it and starts a pause once the threshold is met.
    /// </summary>
    /// <param name="record">The record to update.</param>
    /// <param name="success">Whether the attempt succeeded.</param>
    /// <param name="threshold">The consecutive failure count that triggers a pause. Zero disables backoff.</param>
    /// <param name="window">How long the pause lasts.</param>
    /// <param name="utcNow">The current UTC time.</param>
    public static void ApplyAttempt(DNSRecord record, bool success, int threshold, TimeSpan window, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (success)
        {
            FromState(record, BackoffState.Initial);
            return;
        }

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

    private static BackoffState ToState(DNSRecord record)
        => new(record.ConsecutiveFailures, record.BackoffUntilUtc);

    private static void FromState(DNSRecord record, BackoffState state)
    {
        record.ConsecutiveFailures = state.ConsecutiveFailures;
        record.BackoffUntilUtc = state.BackoffUntilUtc;
    }
}
