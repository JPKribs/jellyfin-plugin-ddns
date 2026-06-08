using System;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Runtime status for one record. Kept in a separate store rather than the saved configuration, so the
/// config holds only authored data and is not rewritten on every update run.
/// </summary>
public sealed class RecordStatus
{
    /// <summary>Gets or sets the last IPv4 address pushed for this record.</summary>
    public string LastIPv4 { get; set; } = string.Empty;

    /// <summary>Gets or sets the last IPv6 address pushed for this record.</summary>
    public string LastIPv6 { get; set; } = string.Empty;

    /// <summary>Gets or sets the message from the most recent update attempt.</summary>
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the outcome label of the most recent run.</summary>
    public string LastAction { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the most recent attempt succeeded.</summary>
    public bool LastSuccess { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the most recent update attempt.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the most recent successful push.</summary>
    public DateTime? LastUpdateUtc { get; set; }

    /// <summary>Gets or sets the number of consecutive failed update attempts.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets the UTC time until which this record is paused by the backoff policy.</summary>
    public DateTime? BackoffUntilUtc { get; set; }

    /// <summary>Copies this status onto a record's runtime fields.</summary>
    /// <param name="record">The record to populate.</param>
    public void ApplyTo(DNSRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.LastIPv4 = LastIPv4;
        record.LastIPv6 = LastIPv6;
        record.LastStatus = LastStatus;
        record.LastAction = LastAction;
        record.LastSuccess = LastSuccess;
        record.LastCheckedUtc = LastCheckedUtc;
        record.LastUpdateUtc = LastUpdateUtc;
        record.ConsecutiveFailures = ConsecutiveFailures;
        record.BackoffUntilUtc = BackoffUntilUtc;
    }

    /// <summary>Reads a record's current runtime fields into a status object.</summary>
    /// <param name="record">The record to read.</param>
    /// <returns>The status snapshot.</returns>
    public static RecordStatus FromRecord(DNSRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RecordStatus
        {
            LastIPv4 = record.LastIPv4,
            LastIPv6 = record.LastIPv6,
            LastStatus = record.LastStatus,
            LastAction = record.LastAction,
            LastSuccess = record.LastSuccess,
            LastCheckedUtc = record.LastCheckedUtc,
            LastUpdateUtc = record.LastUpdateUtc,
            ConsecutiveFailures = record.ConsecutiveFailures,
            BackoffUntilUtc = record.BackoffUntilUtc
        };
    }
}
