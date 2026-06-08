using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Merges the records submitted by the dashboard onto the stored ones. Each incoming record keeps its
/// authored fields, is matched to the stored record by id to resolve its secrets, and a missing or
/// duplicate id is replaced with a fresh unique one so two records never collapse together or cross
/// assign credentials.
/// </summary>
public static class RecordMerge
{
    /// <summary>
    /// Returns the merged record list in the incoming order.
    /// </summary>
    /// <param name="existing">The currently stored records.</param>
    /// <param name="incoming">The submitted records, each mutated in place with its resolved id and secrets.</param>
    /// <param name="resolveSecret">Resolves a submitted secret against the prior stored value.</param>
    /// <param name="newId">Produces a fresh unique id for a missing or duplicate one.</param>
    /// <returns>The merged records.</returns>
    public static List<DNSRecord> Apply(
        IReadOnlyList<DNSRecord> existing,
        IReadOnlyList<DNSRecord> incoming,
        Func<string?, string?, string> resolveSecret,
        Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(resolveSecret);
        ArgumentNullException.ThrowIfNull(newId);

        var previous = existing
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<DNSRecord>(incoming.Count);
        foreach (var record in incoming)
        {
            // Match the existing record by id. A missing or duplicate id gets a fresh unique one.
            if (string.IsNullOrEmpty(record.Id) || !usedIds.Add(record.Id))
            {
                do
                {
                    record.Id = newId();
                }
                while (!usedIds.Add(record.Id));
            }

            previous.TryGetValue(record.Id, out var prior);
            record.Login = resolveSecret(record.Login, prior?.Login);
            record.Password = resolveSecret(record.Password, prior?.Password);
            merged.Add(record);
        }

        return merged;
    }
}
