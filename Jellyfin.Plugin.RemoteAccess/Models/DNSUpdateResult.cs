namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The outcome of a single provider update attempt.
/// </summary>
public sealed class DNSUpdateResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DNSUpdateResult"/> class.
    /// </summary>
    /// <param name="success">Whether the update succeeded.</param>
    /// <param name="message">A human-readable description of the outcome.</param>
    public DNSUpdateResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    /// <summary>Gets a value indicating whether the update succeeded.</summary>
    public bool Success { get; }

    /// <summary>Gets a human-readable description of the outcome.</summary>
    public string Message { get; }

    /// <summary>
    /// Gets a value indicating whether the IPv4 address was pushed successfully: <c>null</c> when no
    /// IPv4 push was attempted, otherwise the per-family outcome. Providers that push each family in its
    /// own request report this so the update cycle records only addresses that actually landed; providers
    /// whose single request carries every family leave it <c>null</c> and <see cref="Success"/> governs.
    /// </summary>
    public bool? IPv4Applied { get; init; }

    /// <summary>
    /// Gets a value indicating whether the IPv6 address was pushed successfully: <c>null</c> when no
    /// IPv6 push was attempted, otherwise the per-family outcome.
    /// </summary>
    public bool? IPv6Applied { get; init; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="message">The outcome message.</param>
    /// <returns>A successful <see cref="DNSUpdateResult"/>.</returns>
    public static DNSUpdateResult Ok(string message) => new(true, message);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">The outcome message.</param>
    /// <returns>A failed <see cref="DNSUpdateResult"/>.</returns>
    public static DNSUpdateResult Fail(string message) => new(false, message);
}
