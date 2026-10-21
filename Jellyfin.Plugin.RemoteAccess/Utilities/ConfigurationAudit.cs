using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Diffs the stored records against a saved set and writes additions, removals, and credential changes
/// to the activity log, so configuration edits stay auditable on servers with more than one
/// administrator. Records are matched by id and credentials are compared in their stored form. An
/// untouched credential round trips with its exact stored value while a newly entered one is always a
/// fresh encryption, so plain string inequality detects a change without ever decrypting anything.
/// </summary>
public static class ConfigurationAudit
{
    /// <summary>
    /// Logs the record level differences between the configuration before and after a save.
    /// </summary>
    /// <param name="activity">The activity log writer, or null when none is available yet.</param>
    /// <param name="before">The records as stored before the save, or null when no configuration existed.</param>
    /// <param name="after">The records as they are being persisted.</param>
    /// <param name="userId">The administrator who made the change, empty when not known.</param>
    public static void LogRecordChanges(ActivityLogger? activity, IReadOnlyList<DNSRecord>? before, IReadOnlyList<DNSRecord>? after, Guid userId = default)
    {
        if (activity is null || after is null)
        {
            return;
        }

        var prior = (before ?? Array.Empty<DNSRecord>())
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in after)
        {
            if (!prior.TryGetValue(record.Id, out var old))
            {
                activity.Log(
                    "Dynamic DNS record " + record.DisplayName() + " was added",
                    "DynamicDNS.RecordAdded",
                    "Provider: " + record.Provider + ", hostname: " + record.Hostname + ".",
                    userId: userId);
                continue;
            }

            kept.Add(record.Id);
            if (SettingsDiffer(old, record))
            {
                activity.Log(
                    "Dynamic DNS record " + record.DisplayName() + " was changed",
                    "DynamicDNS.RecordChanged",
                    (record.Enabled ? "Enabled" : "Disabled") + ". Provider: " + record.Provider + ", hostname: " + record.Hostname + ".",
                    userId: userId);
            }

            if (!string.Equals(record.Login, old.Login, StringComparison.Ordinal)
                || !string.Equals(record.Password, old.Password, StringComparison.Ordinal))
            {
                activity.Log(
                    "Dynamic DNS credentials for " + record.DisplayName() + " were changed",
                    "DynamicDNS.RecordCredentialsChanged",
                    userId: userId);
            }
        }

        foreach (var old in prior.Values.Where(r => !kept.Contains(r.Id)))
        {
            activity.Log(
                "Dynamic DNS record " + old.DisplayName() + " was removed",
                "DynamicDNS.RecordRemoved",
                "Provider: " + old.Provider + ", hostname: " + old.Hostname + ".",
                userId: userId);
        }
    }

    // The fields an administrator sets, leaving out credentials (logged on their own) and the run status
    // the update service keeps on the record.
    private static bool SettingsDiffer(DNSRecord a, DNSRecord b)
        => a.Name != b.Name || a.Provider != b.Provider || a.Hostname != b.Hostname || a.Enabled != b.Enabled
            || a.UpdateIPv4 != b.UpdateIPv4 || a.UpdateIPv6 != b.UpdateIPv6 || a.Zone != b.Zone || a.Server != b.Server
            || a.Ttl != b.Ttl || a.Proxied != b.Proxied || a.Wildcard != b.Wildcard || a.Mx != b.Mx
            || a.BackupMx != b.BackupMx || a.Static != b.Static;
}
