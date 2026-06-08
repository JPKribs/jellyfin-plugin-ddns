using System;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// The failure backoff state machine for a record. After a configured number of consecutive failures a
/// record is paused for a window, so a misconfigured record is not retried against its provider every run
/// and risk a rate limit or ban. It is tried once per window until it succeeds.
/// </summary>
public static class BackoffPolicy
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
        return threshold > 0 && record.BackoffUntilUtc is DateTime until && utcNow < until;
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
            record.ConsecutiveFailures = 0;
            record.BackoffUntilUtc = null;
            return;
        }

        record.ConsecutiveFailures++;
        if (threshold > 0 && record.ConsecutiveFailures >= threshold)
        {
            record.BackoffUntilUtc = utcNow + window;
        }
    }
}
