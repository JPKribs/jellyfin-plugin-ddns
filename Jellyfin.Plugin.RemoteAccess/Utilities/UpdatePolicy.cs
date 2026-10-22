using System;
using System.Net.Sockets;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Decides whether a record should be updated, so we never re-push an address a provider already holds.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>
    /// How long after a successful push an unchanged address is trusted without a live DNS match, when the
    /// record leaves its TTL on automatic. Resolvers keep serving the old answer until it expires, so a
    /// check in that window would otherwise push the same address again.
    /// </summary>
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>The longest grace a large record TTL can earn, so a deleted record is recreated within the hour.</summary>
    internal static readonly TimeSpan MaxGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns the update decision for a record. The detected public IP is compared against what the
    /// hostname currently resolves to in DNS, not a value the plugin stored, so a record changed from an
    /// external source is corrected on the next run. Proxied records are the exception, since their DNS
    /// hides the origin IP, so those compare against the last address that was pushed. An unchanged record
    /// is normally skipped, unless a force interval is set and the last successful push is older than it.
    /// </summary>
    /// <param name="record">The record to evaluate.</param>
    /// <param name="ip">The freshly detected public addresses.</param>
    /// <param name="dns">What the hostname currently resolves to in DNS, or a failed lookup.</param>
    /// <param name="forceInterval">How long an unchanged record may go without a push before one is forced. Zero disables forcing.</param>
    /// <param name="utcNow">The current UTC time, compared against the record's last push.</param>
    /// <returns>The update decision.</returns>
    public static UpdateDecision Decide(DNSRecord record, DetectedIP ip, DNSResolution dns, TimeSpan forceInterval, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(dns);

        if (!record.WantsIPv4(ip) && !record.WantsIPv6(ip))
        {
            return UpdateDecision.SkipNoAddress;
        }

        var (v4, v6) = FamiliesNeedingPush(record, ip, dns, utcNow);
        if (v4 || v6)
        {
            return UpdateDecision.Update;
        }

        // DNS already serves the detected address for every enabled family. Re-push only when a force
        // interval is set and the last successful push is at least that old.
        if (forceInterval > TimeSpan.Zero)
        {
            var lastPush = record.LastUpdateUtc;
            if (lastPush is null || utcNow - lastPush.Value >= forceInterval)
            {
                return UpdateDecision.Update;
            }
        }

        return UpdateDecision.SkipUnchanged;
    }

    /// <summary>
    /// Returns which wanted families need a push, ignoring the force interval. A family that is not wanted
    /// never needs one. The update cycle uses this to leave a family that is already current out of a push
    /// for providers that send each family in its own request.
    /// </summary>
    /// <param name="record">The record to evaluate.</param>
    /// <param name="ip">The freshly detected public addresses.</param>
    /// <param name="dns">What the hostname currently resolves to in DNS, or a failed lookup.</param>
    /// <param name="utcNow">The current UTC time, compared against the record's last push.</param>
    /// <returns>Whether IPv4 and IPv6 each need a push.</returns>
    public static (bool IPv4, bool IPv6) FamiliesNeedingPush(DNSRecord record, DetectedIP ip, DNSResolution dns, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(dns);

        var wantsV4 = record.WantsIPv4(ip);
        var wantsV6 = record.WantsIPv6(ip);
        var v4Ok = LastPushLanded(record, record.FailedIPv4, record.FailedIPv6);
        var v6Ok = LastPushLanded(record, record.FailedIPv6, record.FailedIPv4);

        if (record.Proxied)
        {
            // A proxied record resolves to the proxy, not the origin, so DNS cannot reveal the real IP.
            // Compare the detected IP against the last address pushed.
            return (
                wantsV4 && (!SameAddress(ip.IPv4, record.LastIPv4) || !v4Ok),
                wantsV6 && (!SameAddress(ip.IPv6, record.LastIPv6) || !v6Ok));
        }

        var grace = WithinGrace(record, utcNow);
        return (
            wantsV4 && FamilyNeedsPush(ip.IPv4, record.LastIPv4, v4Ok, grace, dns, AddressFamily.InterNetwork),
            wantsV6 && FamilyNeedsPush(ip.IPv6, record.LastIPv6, v6Ok, grace, dns, AddressFamily.InterNetworkV6));
    }

    /// <summary>
    /// Returns how long after a successful push an unchanged address is trusted without a live DNS match:
    /// the record's TTL, since that is how long a resolver may keep the old answer, capped at an hour. A TTL
    /// left on automatic uses ten minutes.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>The grace window.</returns>
    internal static TimeSpan Grace(DNSRecord record)
    {
        if (record.Ttl <= 1)
        {
            return DefaultGrace;
        }

        var ttl = TimeSpan.FromSeconds(record.Ttl);
        return ttl < MaxGrace ? ttl : MaxGrace;
    }

    private static bool WithinGrace(DNSRecord record, DateTime utcNow)
        => record.LastSuccess
            && record.LastUpdateUtc is { } lastPush
            && utcNow >= lastPush
            && utcNow - lastPush < Grace(record);

    // Whether a family's last push landed. A full success says so for every family. After a partial
    // success the family that failed keeps its failed address and the one that landed has none, so the
    // landed one is not pushed again on the strength of the other's failure.
    private static bool LastPushLanded(DNSRecord record, string failedHere, string failedOther)
        => record.LastSuccess || (string.IsNullOrEmpty(failedHere) && !string.IsNullOrEmpty(failedOther));

    private static bool SameAddress(string? detected, string? lastPushed)
        => string.Equals(detected, lastPushed, StringComparison.OrdinalIgnoreCase);

    // For one family: DNS already serving the detected IP means nothing to do. Just after a successful push
    // of the same address, a resolver still caching the old answer is not a reason to push it again. A
    // failed lookup, or an answer that carries public addresses but none of this family, means the record
    // is missing at the provider, so push to recreate it. A different public address means the record
    // changed elsewhere, so push. Only an answer holding nothing but private addresses (split horizon), or
    // a record whose name is never looked up, falls back to comparing against the last address pushed, which
    // stops a server whose local DNS returns an internal address from re-pushing every run.
    private static bool FamilyNeedsPush(string? detected, string? lastPushed, bool lastOk, bool withinGrace, DNSResolution dns, AddressFamily family)
    {
        if (withinGrace && SameAddress(detected, lastPushed))
        {
            return false;
        }

        if (dns.Serves(detected))
        {
            return false;
        }

        if (dns.Checked)
        {
            if (!dns.Answered || dns.ServesPublic(family))
            {
                return true;
            }

            if (!dns.HasFamily(family) && dns.ServesAnyPublic())
            {
                return true;
            }
        }

        return !SameAddress(detected, lastPushed) || !lastOk;
    }
}
