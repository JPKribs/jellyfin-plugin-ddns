using System;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Decides whether a record should be updated, so we never re-push an address a provider already holds.
/// </summary>
public static class UpdatePolicy
{
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

        var wantsV4 = record.WantsIPv4(ip);
        var wantsV6 = record.WantsIPv6(ip);
        if (!wantsV4 && !wantsV6)
        {
            return UpdateDecision.SkipNoAddress;
        }

        bool needsPush;
        if (record.Proxied)
        {
            // A proxied record (Cloudflare orange cloud) resolves to the proxy, not the origin, so DNS
            // cannot reveal the real IP. Compare against the last address we pushed for these instead.
            var v4Changed = wantsV4 && !string.Equals(ip.IPv4, record.LastIPv4, StringComparison.OrdinalIgnoreCase);
            var v6Changed = wantsV6 && !string.Equals(ip.IPv6, record.LastIPv6, StringComparison.OrdinalIgnoreCase);
            needsPush = v4Changed || v6Changed || !record.LastSuccess;
        }
        else
        {
            // Push when DNS does not already serve the detected address for an enabled family. A failed
            // lookup serves nothing, so a missing record or a transient resolver error pushes rather than
            // skips, which is the safe direction.
            needsPush = (wantsV4 && !dns.Serves(ip.IPv4)) || (wantsV6 && !dns.Serves(ip.IPv6));
        }

        if (needsPush)
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
}
