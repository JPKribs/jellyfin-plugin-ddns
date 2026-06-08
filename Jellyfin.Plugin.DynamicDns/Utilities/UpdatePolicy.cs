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
            needsPush = ProxiedNeedsPush(record, ip, wantsV4, wantsV6);
        }
        else
        {
            needsPush =
                (wantsV4 && FamilyNeedsPush(ip.IPv4, record.LastIPv4, record.LastSuccess, dns, AddressFamily.InterNetwork))
                || (wantsV6 && FamilyNeedsPush(ip.IPv6, record.LastIPv6, record.LastSuccess, dns, AddressFamily.InterNetworkV6));
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

    // For one family: DNS already serving the detected IP means nothing to do. A different public address
    // means the record changed elsewhere, so push. A private answer (split horizon) or no answer cannot
    // confirm the public record, so compare the detected IP against the last one pushed instead, which
    // stops a server whose local DNS returns an internal address from re-pushing every run.
    private static bool FamilyNeedsPush(string? detected, string? lastPushed, bool lastSuccess, DNSResolution dns, AddressFamily family)
    {
        if (dns.Serves(detected))
        {
            return false;
        }

        if (dns.ServesPublic(family))
        {
            return true;
        }

        return !string.Equals(detected, lastPushed, StringComparison.OrdinalIgnoreCase) || !lastSuccess;
    }

    // A proxied record resolves to the proxy, not the origin, so DNS cannot reveal the real IP. Compare the
    // detected IP against the last address pushed for the enabled families.
    private static bool ProxiedNeedsPush(DNSRecord record, DetectedIP ip, bool wantsV4, bool wantsV6)
    {
        var v4Changed = wantsV4 && !string.Equals(ip.IPv4, record.LastIPv4, StringComparison.OrdinalIgnoreCase);
        var v6Changed = wantsV6 && !string.Equals(ip.IPv6, record.LastIPv6, StringComparison.OrdinalIgnoreCase);
        return v4Changed || v6Changed || !record.LastSuccess;
    }
}
