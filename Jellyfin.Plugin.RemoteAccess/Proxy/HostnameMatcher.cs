using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Normalizes and matches hostnames. A pattern is either an exact name or a single-label wildcard
/// (<c>*.example.com</c> matches <c>media.example.com</c> but not <c>example.com</c> or <c>a.b.example.com</c>),
/// the same rule TLS certificates use.
/// </summary>
public static class HostnameMatcher
{
    // Top-level names used only on private networks.
    private static readonly HashSet<string> _privateSuffixes = new(StringComparer.Ordinal)
    {
        "lan", "local", "localdomain", "localhost", "home", "internal", "intranet", "corp", "private", "test", "invalid", "example"
    };

    /// <summary>
    /// Lower-cases and trims a hostname or pattern and strips a trailing dot. Returns an empty string when
    /// the value is not a valid DNS name or wildcard pattern.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The normalized name, or empty when invalid.</returns>
    public static string Normalize(string? value)
    {
        var name = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return IsValidPattern(name) ? name : string.Empty;
    }

    /// <summary>
    /// Whether a value is a valid hostname or single-label wildcard pattern.
    /// </summary>
    /// <param name="name">The normalized value.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidPattern(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253)
        {
            return false;
        }

        var labels = name.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (i == 0 && label == "*")
            {
                continue;
            }

            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            if (!label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        // A wildcard must leave at least two labels after it, so "*.com" is refused.
        return labels[0] != "*" || labels.Length >= 3;
    }

    /// <summary>
    /// Whether a request host matches a pattern.
    /// </summary>
    /// <param name="host">The request host, without port.</param>
    /// <param name="pattern">A normalized pattern.</param>
    /// <returns>True when the host matches.</returns>
    public static bool Matches(string? host, string pattern)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        var name = host.TrimEnd('.');
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var suffix = pattern[1..];
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = name[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a hostname can be on a public certificate: it has a public suffix and is not an IP address
    /// or a name reserved for private networks, such as <c>nas.lan</c> or <c>media.home.arpa</c>.
    /// </summary>
    /// <param name="name">The normalized name.</param>
    /// <returns>True when a certificate authority could check it.</returns>
    public static bool IsPublicName(string name)
    {
        if (string.IsNullOrEmpty(name) || System.Net.IPAddress.TryParse(name, out _))
        {
            return false;
        }

        var labels = name.TrimStart('*', '.').Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        var tld = labels[^1];
        return !_privateSuffixes.Contains(tld) && !name.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a request host matches any of the patterns.
    /// </summary>
    /// <param name="host">The request host, without port.</param>
    /// <param name="patterns">The normalized patterns.</param>
    /// <returns>True when any pattern matches.</returns>
    public static bool MatchesAny(string? host, IEnumerable<string> patterns)
        => patterns.Any(p => Matches(host, p));

    /// <summary>
    /// Normalizes a list of names, dropping invalid entries and duplicates.
    /// </summary>
    /// <param name="values">The raw names.</param>
    /// <returns>The normalized, distinct names.</returns>
    public static List<string> NormalizeAll(IEnumerable<string>? values)
        => (values ?? Enumerable.Empty<string>())
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
