using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// Chooses how to prove the addresses to Let's Encrypt. DNS-01 is used when every name, wildcards included,
/// falls under an enabled DDNS record whose provider can publish TXT records; otherwise HTTP-01 is used for
/// the names that are not wildcards. Pure, so the choice is unit-tested.
/// </summary>
public sealed class AcmePlan
{
    private AcmePlan(AcmeMethod method, List<string> names, Dictionary<string, DNSRecord> records)
    {
        Method = method;
        Names = names;
        Records = records;
    }

    /// <summary>Gets how the names are proved.</summary>
    public AcmeMethod Method { get; }

    /// <summary>Gets the names to put on the certificate, sorted.</summary>
    public List<string> Names { get; }

    /// <summary>Gets, for DNS-01, the record whose provider publishes each name's TXT record.</summary>
    public IReadOnlyDictionary<string, DNSRecord> Records { get; }

    /// <summary>
    /// Builds the plan.
    /// </summary>
    /// <param name="hostnames">The proxy's hostnames.</param>
    /// <param name="records">The DDNS records.</param>
    /// <param name="supportsTxt">Whether a provider can publish TXT records.</param>
    /// <returns>The plan.</returns>
    public static AcmePlan For(IEnumerable<string> hostnames, IEnumerable<DNSRecord> records, Func<DNSProviderKind, bool> supportsTxt)
    {
        ArgumentNullException.ThrowIfNull(supportsTxt);
        var all = (hostnames ?? Enumerable.Empty<string>())
            .Select(h => (h ?? string.Empty).Trim().ToLowerInvariant().TrimEnd('.'))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        var usable = (records ?? Enumerable.Empty<DNSRecord>())
            .Where(r => r.Enabled && supportsTxt(r.Provider))
            .Select(r => (Record: r, Domain: Domain(r)))
            .Where(r => r.Domain.Length > 0)
            .ToList();

        var byName = new Dictionary<string, DNSRecord>(StringComparer.Ordinal);
        foreach (var name in all)
        {
            var bare = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;

            // The most specific domain wins, so a record for the exact name beats one for its zone.
            var match = usable
                .Where(r => bare == r.Domain || bare.EndsWith("." + r.Domain, StringComparison.Ordinal))
                .OrderByDescending(r => r.Domain.Length)
                .Select(r => r.Record)
                .FirstOrDefault();
            if (match is null)
            {
                return new AcmePlan(AcmeMethod.Http, AcmePlanner.Names(all), new Dictionary<string, DNSRecord>());
            }

            byName[name] = match;
        }

        return all.Count == 0
            ? new AcmePlan(AcmeMethod.Http, new List<string>(), byName)
            : new AcmePlan(AcmeMethod.Dns, all, byName);
    }

    /// <summary>
    /// The TXT name Let's Encrypt looks up for a name: the wildcard's base for a wildcard.
    /// </summary>
    /// <param name="name">The certificate name.</param>
    /// <returns>The TXT record name.</returns>
    public static string TxtName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "_acme-challenge." + (name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name);
    }

    // A record proves names in its zone when it names one, otherwise its own name and the names under it.
    private static string Domain(DNSRecord record)
        => (record.Zone.Trim().Length > 0 ? record.Zone : RecordTarget.ResolvableName(record) ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .TrimEnd('.');
}
