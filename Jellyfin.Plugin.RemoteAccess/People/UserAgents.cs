using System;
using System.Buffers;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Turns a User-Agent header into a few words, like <c>Firefox, Windows</c>. Only the common cases are named;
/// anything else falls back to the first product token, so scanners and scripts show as what they claim to be.
/// </summary>
public static class UserAgents
{
    private const int MaxLength = 60;
    private static readonly SearchValues<char> _tokenEnd = SearchValues.Create(" (");

    /// <summary>
    /// Describes a User-Agent header.
    /// </summary>
    /// <param name="userAgent">The header value.</param>
    /// <returns>A short description, or empty when there is none.</returns>
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return string.Empty;
        }

        var ua = userAgent.Trim();
        var browser = Has(ua, "Edg/") ? "Edge"
            : Has(ua, "OPR/") ? "Opera"
            : Has(ua, "Firefox/") ? "Firefox"
            : Has(ua, "Chrome/") ? "Chrome"
            : Has(ua, "Safari/") ? "Safari"
            : string.Empty;
        var os = Has(ua, "Android") ? "Android"
            : Has(ua, "iPhone") || Has(ua, "iPad") ? "iOS"
            : Has(ua, "Windows") ? "Windows"
            : Has(ua, "Mac OS X") || Has(ua, "Macintosh") ? "macOS"
            : Has(ua, "CrOS") ? "ChromeOS"
            : Has(ua, "Linux") ? "Linux"
            : string.Empty;
        if (browser.Length > 0)
        {
            return os.Length > 0 ? browser + ", " + os : browser;
        }

        var end = ua.AsSpan().IndexOfAny(_tokenEnd);
        var product = end > 0 ? ua[..end] : ua;
        return product.Length > MaxLength ? product[..MaxLength] : product;
    }

    private static bool Has(string ua, string token) => ua.Contains(token, StringComparison.OrdinalIgnoreCase);
}
