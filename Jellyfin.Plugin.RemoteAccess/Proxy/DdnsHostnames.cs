using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Lists the hostnames the DDNS records keep pointed at this server, so the proxy's hostname fields can
/// offer them instead of having them typed again.
/// </summary>
public static class DdnsHostnames
{
    /// <summary>
    /// Builds the choices: each record's full name (as its live DNS is checked), plus the wildcard under it
    /// when the record also updates a wildcard. Records whose name cannot be worked out, such as those keyed
    /// by a numeric record id, are left out.
    /// </summary>
    /// <param name="records">The DDNS records.</param>
    /// <returns>Distinct choices, enabled records first.</returns>
    public static List<DdnsHostname> From(IEnumerable<DNSRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var result = new List<DdnsHostname>();
        foreach (var record in records)
        {
            var name = HostnameMatcher.Normalize(RecordTarget.ResolvableName(record));
            if (name.Length == 0)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(record.Name) ? name : record.Name.Trim();
            result.Add(new DdnsHostname(name, label, record.Enabled));
            var wildcard = HostnameMatcher.Normalize("*." + name);
            if (record.Wildcard && wildcard.Length > 0)
            {
                result.Add(new DdnsHostname(wildcard, label, record.Enabled));
            }
        }

        return result
            .GroupBy(h => h.Hostname, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Enabled).First())
            .OrderByDescending(h => h.Enabled)
            .ThenBy(h => h.Hostname.StartsWith("*.", StringComparison.Ordinal) ? h.Hostname[2..] : h.Hostname, StringComparer.Ordinal)
            .ThenBy(h => h.Hostname.StartsWith('*'))
            .ToList();
    }
}
