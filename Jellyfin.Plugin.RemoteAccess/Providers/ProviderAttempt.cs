using System.Threading;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Watches the HTTP responses a provider receives during one update attempt, so the update cycle can tell
/// a temporary failure (the provider could not be reached or answered with a server error) from a
/// definitive one (the provider rejected the request) without every provider classifying its own errors.
/// The attempt flows to the provider through an async local, so concurrent attempts never share one.
/// </summary>
internal sealed class ProviderAttempt
{
    private static readonly AsyncLocal<ProviderAttempt?> CurrentAttempt = new();

    // Status of the most recent response, or -1 before any request was sent.
    private int _lastStatus = -1;

    // Status of the most recent response within the address family being pushed, or -1 before any.
    private int _familyStatus = -1;

    // Whether any response or family outcome was definitive (rejected credentials, a rejected request,
    // or an error reported in a reply body), which no later temporary error can outweigh.
    private bool _sawDefinitive;

    internal ProviderAttempt(CancellationToken runToken = default)
    {
        RunToken = runToken;
    }

    /// <summary>Gets the attempt the current async flow is reporting to, or null outside an attempt.</summary>
    internal static ProviderAttempt? Current => CurrentAttempt.Value;

    /// <summary>
    /// Gets the token of the run (or server shutdown) the attempt belongs to, as opposed to the per request
    /// timeout. A cancellation that is not this token's is the request timing out.
    /// </summary>
    internal CancellationToken RunToken { get; }

    /// <summary>
    /// Gets a value indicating whether a failed attempt was temporary: nothing definitive was seen, and the
    /// final response never arrived (status zero, a timeout or network error), was a request timeout or rate
    /// limit, or was a server error. A failure decided before any request is definitive.
    /// </summary>
    internal bool FailureIsTransient => !_sawDefinitive && IsTransientStatus(_lastStatus);

    /// <summary>
    /// Gets a value indicating whether DNS already serves the detected IPv4, so a provider that pushes each
    /// family in its own request may leave IPv4 out while it pushes IPv6.
    /// </summary>
    internal bool HoldIPv4 { get; init; }

    /// <summary>
    /// Gets a value indicating whether DNS already serves the detected IPv6, so a provider that pushes each
    /// family in its own request may leave IPv6 out while it pushes IPv4.
    /// </summary>
    internal bool HoldIPv6 { get; init; }

    /// <summary>Gets or sets a value indicating whether the provider actually left IPv4 out of the push.</summary>
    internal bool IPv4Held { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider actually left IPv6 out of the push.</summary>
    internal bool IPv6Held { get; set; }

    /// <summary>
    /// Starts a new attempt for the current async flow. The caller's flow sees the attempt, and so does
    /// everything it awaits; the value is restored when the calling async method returns.
    /// </summary>
    /// <param name="runToken">The run's own cancellation token, without the per request timeout.</param>
    /// <returns>The attempt that provider requests in this flow report to.</returns>
    internal static ProviderAttempt Begin(CancellationToken runToken = default)
        => Begin(holdIPv4: false, holdIPv6: false, runToken);

    /// <summary>
    /// Starts a new attempt for the current async flow, naming a family that is already current and may
    /// be left out by a provider that pushes each family in its own request.
    /// </summary>
    /// <param name="holdIPv4">Whether IPv4 is already current.</param>
    /// <param name="holdIPv6">Whether IPv6 is already current.</param>
    /// <param name="runToken">The run's own cancellation token, without the per request timeout.</param>
    /// <returns>The attempt that provider requests in this flow report to.</returns>
    internal static ProviderAttempt Begin(bool holdIPv4, bool holdIPv6, CancellationToken runToken)
    {
        var attempt = new ProviderAttempt(runToken) { HoldIPv4 = holdIPv4, HoldIPv6 = holdIPv6 };
        CurrentAttempt.Value = attempt;
        return attempt;
    }

    /// <summary>Returns whether an HTTP status (zero when no response arrived) is a temporary failure.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <returns>True for no response, 408, 429, and any 5xx.</returns>
    internal static bool IsTransientStatus(int status)
        => status == 0 || status == 408 || status == 429 || status >= 500;

    /// <summary>Records one response.</summary>
    /// <param name="status">The HTTP status code, or zero when no response arrived.</param>
    internal void Observe(int status)
    {
        _lastStatus = status;
        _familyStatus = status;

        // Credentials rejected anywhere are never temporary, even when a lookup is followed by a write.
        _sawDefinitive |= status == 401 || status == 403;
    }

    /// <summary>
    /// Records the outcome of one address family's push. A family that failed on anything but a temporary
    /// final response (a 4xx, an error in a 2xx reply body, or a check before any request) is definitive,
    /// so a later family's server error cannot make the whole attempt look temporary.
    /// </summary>
    /// <param name="ok">Whether the family's push succeeded.</param>
    internal void CompleteFamily(bool ok)
    {
        if (!ok && !IsTransientStatus(_familyStatus))
        {
            _sawDefinitive = true;
        }

        _familyStatus = -1;
    }

    /// <summary>Records a family that timed out without any response.</summary>
    internal void FamilyTimedOut()
    {
        _lastStatus = 0;
        _familyStatus = 0;
    }
}
