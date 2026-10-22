using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// What happened on the last attempts to get a certificate from Let's Encrypt, kept so a failing setup backs
/// off instead of running into Let's Encrypt's rate limits.
/// </summary>
public sealed class AcmeState
{
    /// <summary>Gets or sets when the last attempt started (UTC).</summary>
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>Gets or sets when a certificate was last issued (UTC).</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets how many attempts in a row have failed.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets why the last attempt failed, empty after a success.</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Let's Encrypt refused the last attempt for its rate limits. Even
    /// an administrator's request then waits out the backoff, since asking again only prolongs the limit.
    /// </summary>
    public bool RateLimited { get; set; }

    /// <summary>Gets or sets the names of the last attempt.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>Gets or sets the ACME directory that issued the current certificate, empty when not known.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the certificate from the last issue starts (UTC), to tell it from an older file; null
    /// for state saved by versions that did not record it.
    /// </summary>
    public DateTime? IssuedNotBeforeUtc { get; set; }

    /// <summary>
    /// The state after a failed attempt. What is known about the last issued certificate is kept.
    /// </summary>
    /// <param name="now">When it failed (UTC).</param>
    /// <param name="error">Why.</param>
    /// <param name="names">The names it asked for.</param>
    /// <param name="rateLimited">Whether Let's Encrypt refused it for its rate limits.</param>
    /// <returns>The new state.</returns>
    public AcmeState Failed(DateTime now, string error, IEnumerable<string> names, bool rateLimited = false)
    {
        var attempted = new List<string>(names);
        return new AcmeState
        {
            LastAttemptUtc = now,
            LastSuccessUtc = LastSuccessUtc,

            // Different names start a new streak, so fixing the address is retried at once.
            ConsecutiveFailures = attempted.SequenceEqual(Names, StringComparer.Ordinal) ? ConsecutiveFailures + 1 : 1,
            LastError = error,
            RateLimited = rateLimited,
            Names = attempted,
            Directory = Directory,
            IssuedNotBeforeUtc = IssuedNotBeforeUtc
        };
    }

    /// <summary>
    /// The state after a certificate was issued.
    /// </summary>
    /// <param name="now">When it was issued (UTC).</param>
    /// <param name="names">The names it covers.</param>
    /// <param name="directory">The ACME directory that issued it.</param>
    /// <param name="notBefore">When the certificate starts (UTC).</param>
    /// <returns>The new state.</returns>
    public static AcmeState Succeeded(DateTime now, IEnumerable<string> names, string directory, DateTime? notBefore = null) => new()
    {
        LastAttemptUtc = now,
        LastSuccessUtc = now,
        ConsecutiveFailures = 0,
        LastError = string.Empty,
        Names = new List<string>(names),
        Directory = directory,
        IssuedNotBeforeUtc = notBefore
    };
}
