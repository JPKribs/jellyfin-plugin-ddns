using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Masks credentials before text is written anywhere. Jellyfin accepts tokens in the query string for streams,
/// images and downloads, and DNS providers take keys, passwords and hashes in query strings, paths, JSON bodies,
/// URL userinfo and authorization headers, so any logged URL or reply can carry a working credential. Anything
/// that looks like one is masked, even when it may not be.
/// </summary>
public static partial class CredentialRedactor
{
    /// <summary>The replacement for a masked value.</summary>
    public const string Mask = "***";

    // Shorter values are left alone: masking every "on" or "a" a secret happens to equal would garble the text
    // without hiding anything a guess could not.
    private const int MinimumSecretLength = 4;

    /// <summary>
    /// Masks credential query and form parameters (<c>api_key</c>, <c>token</c>, <c>key</c>, <c>k</c>,
    /// <c>password</c>, <c>pass</c>, <c>pwd</c>, <c>secret</c>, <c>sha</c>, <c>signature</c> and others), the same
    /// names as JSON or XML values, URL userinfo, authorization header values and the <c>Token="..."</c> part of
    /// Jellyfin authorization values anywhere in a piece of text. The parameter name is kept, so the log still
    /// shows a credential was present.
    /// </summary>
    /// <param name="text">The text, for example a URL or a log message.</param>
    /// <returns>The text with credentials masked.</returns>
    public static string Redact(string? text) => Redact(text, Array.Empty<string?>());

    /// <summary>
    /// Masks credentials as <see cref="Redact(string?)"/> does, and also every occurrence of the given secrets,
    /// as they are and URL encoded, wherever they appear. Pass the decrypted credentials of the request the text
    /// came from, so a provider that echoes one back in any form still never gets it logged.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="secrets">The credentials in use. Empty and very short values are skipped.</param>
    /// <returns>The text with credentials masked.</returns>
    public static string Redact(string? text, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = text;

        // The exact values first, longest first, so a secret that contains another is masked whole.
        foreach (var secret in secrets
            .Where(s => !string.IsNullOrEmpty(s) && s.Length >= MinimumSecretLength)
            .SelectMany(s => new[] { s!, Uri.EscapeDataString(s!) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length))
        {
            masked = masked.Replace(secret, Mask, StringComparison.Ordinal);
        }

        masked = UserInfo().Replace(masked, m => m.Groups["scheme"].Value + Mask + "@");
        masked = AuthorizationValue().Replace(masked, m => m.Groups["scheme"].Value + " " + Mask);
        masked = QueryCredential().Replace(masked, m => m.Groups["name"].Value + "=" + Mask);
        masked = QuotedCredential().Replace(masked, m => m.Groups["lead"].Value + Mask + m.Groups["quote"].Value);
        masked = XmlCredential().Replace(masked, m => m.Groups["open"].Value + Mask + m.Groups["close"].Value);
        return TokenAttribute().Replace(masked, m => m.Groups["name"].Value + "=\"" + Mask + "\"");
    }

    // The parameter names providers and Jellyfin use for credentials. Kept in one place so the query, JSON and XML
    // patterns always agree.
    private const string Names = "api_key|apikey|api-key|api_token|apitoken|api-token|api_secret|apisecret|token|access_token|auth_token|"
        + "authtoken|auth|key|k|secret|client_secret|pass|passwd|password|pwd|pw|domainpassword|sha|hash|sig|signature|"
        + "x-auth-key|x-api-key|x-api-secret|x-nsone-key|pddtoken";

    [GeneratedRegex(@"(?<name>(?<=[?&;\s,/]|^)(?:" + Names + @"))=(?!"")[^&#\s""]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    // "password": "value", 'token':'value', password: "value" in JSON-like bodies and replies.
    [GeneratedRegex(@"(?<lead>[""']?\b(?:" + Names + @")[""']?\s*:\s*(?<quote>[""']))[^""']*\k<quote>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedCredential();

    // <password>value</password> in XML replies.
    [GeneratedRegex(@"(?<open><(?<tag>" + Names + @")>)[^<]*(?<close></\k<tag>>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlCredential();

    // https://user:password@host and https://token@host.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/\s@?#]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();

    // Authorization: Basic ..., Bearer ..., sso-key key:secret and the like.
    [GeneratedRegex(@"(?<scheme>\b(?:Basic|Bearer|sso-key|Digest|ApiKey|Token))\s+[A-Za-z0-9._~+/=:-]{6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(@"(?<name>\bToken)=""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenAttribute();
}
