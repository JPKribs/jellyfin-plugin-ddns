using System;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>The decision on whether a record needs to be pushed to its provider this run.</summary>
public enum UpdateDecision
{
    /// <summary>Push the record now.</summary>
    Update,

    /// <summary>Skip: no usable address for the record's enabled families.</summary>
    SkipNoAddress,

    /// <summary>Skip: the address is unchanged since the last successful update.</summary>
    SkipUnchanged
}

/// <summary>
/// Decides whether a record should be updated, so we never re-push an address a provider already holds.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Returns the update decision for a record. A record is pushed when an enabled family's detected
    /// address differs from the last one stored, or when the previous attempt did not succeed (so
    /// failures are retried). It is skipped only when the address is unchanged <em>and</em> the last run
    /// succeeded — avoiding needless provider calls.
    /// </summary>
    /// <param name="record">The record to evaluate (carries the last pushed addresses and success flag).</param>
    /// <param name="ip">The freshly detected public addresses.</param>
    /// <returns>The update decision.</returns>
    public static UpdateDecision Decide(DnsRecord record, DetectedIp ip)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        var wantsV4 = record.UpdateIPv4 && ip.IPv4 is not null;
        var wantsV6 = record.UpdateIPv6 && ip.IPv6 is not null;
        if (!wantsV4 && !wantsV6)
        {
            return UpdateDecision.SkipNoAddress;
        }

        if (!record.LastSuccess)
        {
            return UpdateDecision.Update;
        }

        var v4Changed = wantsV4 && !string.Equals(ip.IPv4, record.LastIPv4, StringComparison.OrdinalIgnoreCase);
        var v6Changed = wantsV6 && !string.Equals(ip.IPv6, record.LastIPv6, StringComparison.OrdinalIgnoreCase);
        return v4Changed || v6Changed ? UpdateDecision.Update : UpdateDecision.SkipUnchanged;
    }
}
