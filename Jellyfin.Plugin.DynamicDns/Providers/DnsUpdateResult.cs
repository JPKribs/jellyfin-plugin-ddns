namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// The outcome of a single provider update attempt.
/// </summary>
public sealed class DnsUpdateResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DnsUpdateResult"/> class.
    /// </summary>
    /// <param name="success">Whether the update succeeded.</param>
    /// <param name="message">A human-readable description of the outcome.</param>
    public DnsUpdateResult(bool success, string message)
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
    /// <returns>A successful <see cref="DnsUpdateResult"/>.</returns>
    public static DnsUpdateResult Ok(string message) => new(true, message);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">The outcome message.</param>
    /// <returns>A failed <see cref="DnsUpdateResult"/>.</returns>
    public static DnsUpdateResult Fail(string message) => new(false, message);
}
