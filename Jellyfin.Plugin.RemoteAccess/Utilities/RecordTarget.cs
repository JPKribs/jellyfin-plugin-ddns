using System;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Works out what a record points at: the fully qualified name its live DNS can be checked against, and
/// whether an edit changed which record or account it targets. The Hostname field means different things
/// per provider (a full name, a name relative to the zone, a DuckDNS label, a numeric record id), so the
/// name to resolve is built per provider rather than read straight from the field.
/// </summary>
public static class RecordTarget
{
    private const string DuckDnsSuffix = ".duckdns.org";

    /// <summary>
    /// Returns the fully qualified name to resolve when checking the record's live DNS, or null when no
    /// name can be built (a numeric record id, several DuckDNS labels, a bare label with no domain). A
    /// null means the live check is skipped and the record is compared against the last address pushed.
    /// An IP literal is never returned.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>The name to resolve, or null.</returns>
    public static string? ResolvableName(DNSRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var host = Clean(record.Hostname);

        return record.Provider switch
        {
            // The Hostname field holds a numeric record id, not a name.
            DNSProviderKind.DnsMadeEasy or DNSProviderKind.Sitelutions => null,

            DNSProviderKind.DuckDns => DuckDnsName(host),

            // These APIs take the record name relative to the zone, so even a dotted name outside the zone
            // is written beneath it.
            DNSProviderKind.Hetzner or DNSProviderKind.Gandi or DNSProviderKind.GoDaddy
                => InZone(host, Clean(record.Zone), dottedIsRelative: true),

            // These take a full name, or a bare label the zone completes.
            DNSProviderKind.Cloudflare or DNSProviderKind.DigitalOcean or DNSProviderKind.DnsExit2
                or DNSProviderKind.Ns1 or DNSProviderKind.Nfsn or DNSProviderKind.Porkbun
                or DNSProviderKind.Dynadot or DNSProviderKind.Dynu or DNSProviderKind.Spaceship
                or DNSProviderKind.Dinahosting or DNSProviderKind.SimplyCom or DNSProviderKind.ZoneEdit1
                => InZone(host, Clean(record.Zone), dottedIsRelative: false),

            _ => AsFqdn(host)
        };
    }

    /// <summary>
    /// Returns whether two versions of a record target the same DNS record with the same account: the
    /// provider, hostname, zone, server override, and stored credentials all match. Credentials compare in
    /// their stored form, so an untouched one matches and a newly entered one never does.
    /// </summary>
    /// <param name="a">One version of the record.</param>
    /// <param name="b">The other version.</param>
    /// <returns>True when both target the same record with the same credentials.</returns>
    public static bool SameTarget(DNSRecord a, DNSRecord b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Provider == b.Provider
            && string.Equals(Clean(a.Hostname), Clean(b.Hostname), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Clean(a.Zone), Clean(b.Zone), StringComparison.OrdinalIgnoreCase)
            && string.Equals((a.Server ?? string.Empty).Trim(), (b.Server ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Login ?? string.Empty, b.Login ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.Password ?? string.Empty, b.Password ?? string.Empty, StringComparison.Ordinal);
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim().TrimEnd('.');

    private static string? DuckDnsName(string labels)
    {
        // Several comma separated labels share one push, so no single name stands for the record.
        if (labels.Length == 0 || labels.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        if (labels.EndsWith(DuckDnsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return AsFqdn(labels);
        }

        return labels.Contains('.', StringComparison.Ordinal) ? null : AsFqdn(labels + DuckDnsSuffix);
    }

    private static string? InZone(string host, string zone, bool dottedIsRelative)
    {
        // A zone given as an id rather than a domain cannot complete a name.
        if (AsFqdn(zone) is null)
        {
            return AsFqdn(host);
        }

        if (host.Length == 0 || host == "@")
        {
            return zone;
        }

        if (string.Equals(host, zone, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
        {
            return AsFqdn(host);
        }

        if (dottedIsRelative || !host.Contains('.', StringComparison.Ordinal))
        {
            return AsFqdn(host + "." + zone);
        }

        return AsFqdn(host);
    }

    // A resolvable name has at least two labels, is a valid DNS name, and is not an IP literal (which also
    // rules out the legacy numeric forms such as "123456" that the IP parser accepts).
    private static string? AsFqdn(string name)
    {
        if (name.Length == 0
            || !name.Contains('.', StringComparison.Ordinal)
            || IPAddress.TryParse(name, out _)
            || Uri.CheckHostName(name) != UriHostNameType.Dns)
        {
            return null;
        }

        return name;
    }
}
