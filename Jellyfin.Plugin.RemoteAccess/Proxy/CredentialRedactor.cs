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

    [GeneratedRegex(@"(?<name>(?<=[?&;\s]|^)(?:api_key|apikey|api-key|token|access_token))=(?!"")[^&#\s""]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    [GeneratedRegex(@"(?<name>\bToken)=""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenAttribute();
}
