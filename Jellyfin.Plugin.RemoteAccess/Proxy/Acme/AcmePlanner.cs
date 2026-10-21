using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// Decides whether to ask Let's Encrypt for a certificate. Pure, so the timing rules are unit-tested.
/// </summary>
public static class AcmePlanner
{
    /// <summary>How long before expiry a certificate is renewed. Let's Encrypt suggests a third of its life.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private static readonly TimeSpan _firstBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromHours(24);

    // A certificate is never ordered again this soon after the last one for the same names. A clock running far
    // ahead would otherwise see every new certificate as expiring, and order one at every trigger.
    private static readonly TimeSpan _minInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The names Let's Encrypt can check over HTTP-01: every configured name except wildcards, which need
    /// DNS-01. Sorted, so the same names always make the same order.
    /// </summary>
    /// <param name="hostnames">The proxy's hostnames.</param>
    /// <returns>The names to put on the certificate.</returns>
    public static List<string> Names(IEnumerable<string> hostnames)
        => (hostnames ?? Enumerable.Empty<string>())
            .Select(h => (h ?? string.Empty).Trim().ToLowerInvariant())
            .Where(h => h.Length > 0 && !h.StartsWith('*'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// How long to wait after a run of failures: an hour, doubling, at most a day.
    /// </summary>
    /// <param name="failures">Failures in a row.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Backoff(int failures)
    {
        if (failures <= 0)
        {
            return TimeSpan.Zero;
        }

        var hours = Math.Pow(2, Math.Min(failures - 1, 10));
        var wait = TimeSpan.FromHours(_firstBackoff.TotalHours * hours);
        return wait < _maxBackoff ? wait : _maxBackoff;
    }

    /// <summary>
    /// Resets times recorded in the future, left by a clock that was wrong, to now. Done once when the state is
    /// read, so the backoff counts from then rather than starting over on every check.
    /// </summary>
    /// <param name="state">The stored state.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The state to use, the same instance when nothing was in the future.</returns>
    public static AcmeState Normalize(AcmeState state, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LastAttemptUtc <= now && (state.LastSuccessUtc is null || state.LastSuccessUtc <= now))
        {
            return state;
        }

        if (state.LastAttemptUtc is null && state.LastSuccessUtc is null)
        {
            return state;
        }

        return new AcmeState
        {
            LastAttemptUtc = state.LastAttemptUtc > now ? now : state.LastAttemptUtc,
            LastSuccessUtc = state.LastSuccessUtc > now ? now : state.LastSuccessUtc,
            ConsecutiveFailures = state.ConsecutiveFailures,
            LastError = state.LastError,
            Names = state.Names,
            Directory = state.Directory,
            IssuedNotBeforeUtc = state.IssuedNotBeforeUtc
        };
    }

    /// <summary>
    /// Decides what to do now.
    /// </summary>
    /// <param name="state">The record of earlier attempts.</param>
    /// <param name="names">The names wanted, from <see cref="Names"/>.</param>
    /// <param name="certificateNames">The names the loaded certificate covers, empty when none is loaded.</param>
    /// <param name="notAfterUtc">When the loaded certificate expires, null when none is loaded.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="notBeforeUtc">When the loaded certificate became valid, null when not known.</param>
    /// <returns>The decision.</returns>
    public static AcmeDecision Decide(AcmeState state, IReadOnlyList<string> names, IReadOnlyCollection<string> certificateNames, DateTime? notAfterUtc, DateTime now, DateTime? notBeforeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(certificateNames);
        if (names.Count == 0)
        {
            return new AcmeDecision(AcmeAction.NoNames);
        }

        var covers = notAfterUtc is not null && names.All(n => certificateNames.Contains(n, StringComparer.OrdinalIgnoreCase));
        var sameNames = state.Names.Count == 0 || state.Names.SequenceEqual(names, StringComparer.Ordinal);

        // The certificate this plugin was issued for these names renews two thirds into its own lifetime,
        // counted from when it was issued on this server's clock. A clock that runs fast or slow then still
        // waits the right time, instead of seeing a fresh certificate as expired and ordering one daily. Only the
        // certificate from that issue counts: an older file put back in its place is judged by its own dates below.
        if (covers && notBeforeUtc is { } from && state.LastSuccessUtc is { } issued && state.Names.SequenceEqual(names, StringComparer.Ordinal)
            && (state.IssuedNotBeforeUtc is not { } recorded || (from - recorded).Duration() < TimeSpan.FromSeconds(1)))
        {
            var renewAt = issued + ((notAfterUtc!.Value - from) * 2 / 3);
            if (now < renewAt && now >= issued)
            {
                return new AcmeDecision(AcmeAction.None);
            }
        }

        AcmeAction wanted;
        if (!covers || notAfterUtc <= now)
        {
            wanted = AcmeAction.Issue;
        }
        else if (notAfterUtc - now < RenewBefore)
        {
            wanted = AcmeAction.Renew;
        }
        else
        {
            return new AcmeDecision(AcmeAction.None);
        }

        // A failure streak waits out its backoff, unless the names changed: a fixed address deserves a retry.
        // Times in the future are reset to now once, when the state is read (see Normalize).
        if (state.ConsecutiveFailures > 0 && state.LastAttemptUtc is { } last && sameNames)
        {
            var next = last + Backoff(state.ConsecutiveFailures);
            if (now < next)
            {
                return new AcmeDecision(AcmeAction.Wait, next);
            }
        }

        // Only a working certificate from this directory for these names holds a new order back; a missing,
        // broken or staging one is replaced at once.
        if (covers && notAfterUtc > now && state.LastSuccessUtc is { } success && state.Names.SequenceEqual(names, StringComparer.Ordinal))
        {
            var next = success + _minInterval;
            if (now < next)
            {
                return new AcmeDecision(AcmeAction.Wait, next);
            }
        }

        return new AcmeDecision(wanted);
    }
}
