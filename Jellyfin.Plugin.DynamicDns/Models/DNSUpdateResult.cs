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

    /// <summary>Creates a successful result.</summary>
    /// <param name="message">The outcome message.</param>
    /// <returns>A successful <see cref="DNSUpdateResult"/>.</returns>
    public static DNSUpdateResult Ok(string message) => new(true, message);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">The outcome message.</param>
    /// <returns>A failed <see cref="DNSUpdateResult"/>.</returns>
    public static DNSUpdateResult Fail(string message) => new(false, message);
}
