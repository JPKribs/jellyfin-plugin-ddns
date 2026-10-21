using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Masks login tokens before text is written anywhere. Jellyfin accepts tokens in the query string for
/// streams, images and downloads, so any logged URL can carry a working credential.
/// </summary>
public static partial class CredentialRedactor
{
    /// <summary>The replacement for a masked value.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Masks credential query parameters (<c>api_key</c>, <c>ApiKey</c>, <c>api-key</c>, <c>token</c>,
    /// <c>access_token</c>) and the <c>Token="..."</c> part of Jellyfin authorization values anywhere in a
    /// piece of text, keeping the parameter name so the log still shows a token was present.
    /// </summary>
    /// <param name="text">The text, for example a URL or a log message.</param>
    /// <returns>The text with credentials masked.</returns>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = QueryCredential().Replace(text, m => m.Groups["name"].Value + "=" + Mask);
        return TokenAttribute().Replace(masked, m => m.Groups["name"].Value + "=\"" + Mask + "\"");
    }

    /// <summary>
    /// Masks a header value when the header carries credentials, otherwise returns it unchanged.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <returns>The value, masked when sensitive.</returns>
    public static string RedactHeader(string name, string? value)
    {
        if (IsCredentialHeader(name))
        {
            return Mask;
        }

        return Redact(value);
    }

    /// <summary>
    /// Whether a header carries credentials.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>True for token, authorization and cookie headers.</returns>
    public static bool IsCredentialHeader(string name)
        => name.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-MediaBrowser-Token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<name>(?<=[?&;\s]|^)(?:api_key|apikey|api-key|token|access_token))=(?!"")[^&#\s""]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    [GeneratedRegex(@"(?<name>\bToken)=""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenAttribute();
}
