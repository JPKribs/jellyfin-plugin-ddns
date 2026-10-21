using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Classifies Jellyfin request paths. Every method takes the path with Jellyfin's base URL already removed
/// and compares segments case-insensitively, the way Jellyfin's own routing does.
/// </summary>
public static class PathRules
{
    // The setup wizard. Jellyfin locks it once setup completes, but on a fresh or reset server it lets anyone
    // create the first administrator, so it is never reachable from the internet.
    private static readonly string[] _startup = { "/Startup" };

    // Streams a client may pause. Kestrel's minimum send rate would otherwise cut a paused player off.
    private static readonly string[] _media =
    {
        "/Videos",
        "/Audio",
        "/LiveTv/LiveStreamFiles",
        "/LiveTv/LiveRecordings",
        "/Items"
    };

    /// <summary>
    /// Removes the base URL prefix, if present.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="baseUrl">Jellyfin's base URL, empty or like <c>/jellyfin</c>.</param>
    /// <returns>The path relative to the base URL.</returns>
    public static PathString StripBase(PathString path, string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return path;
        }

        return path.StartsWithSegments(baseUrl, StringComparison.OrdinalIgnoreCase, out var rest) ? rest : path;
    }

    /// <summary>Whether a path is the setup wizard.</summary>
    /// <param name="path">The base-relative path.</param>
    /// <returns>True for the setup wizard.</returns>
    public static bool IsStartup(PathString path) => StartsWithAny(path, _startup);

    /// <summary>Whether a path is Quick Connect sign-in, which answers a wrong secret with 404.</summary>
    /// <param name="path">The base-relative path.</param>
    /// <returns>True for Quick Connect sign-in.</returns>
    public static bool IsQuickConnectSignIn(PathString path)
        => Trim(path).Equals("/Users/AuthenticateWithQuickConnect", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is one of Jellyfin's sign-in endpoints.</summary>
    /// <param name="path">The base-relative path.</param>
    /// <returns>True for password and Quick Connect sign-in.</returns>
    public static bool IsSignIn(PathString path)
    {
        // Jellyfin's routing ignores a trailing slash, so the rules must too.
        var trimmed = Trim(path);
        if (trimmed.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/Users/AuthenticateWithQuickConnect", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The older POST /Users/{id}/Authenticate?pw= still signs in.
        var segments = Segments(trimmed);
        return segments.Length == 3
            && segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("Authenticate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a path may stream media. Only download, stream and item file paths are matched precisely
    /// under <c>/Items</c>; other item APIs keep the minimum send rate.
    /// </summary>
    /// <param name="path">The base-relative path.</param>
    /// <returns>True for media streams.</returns>
    public static bool IsMedia(PathString path)
    {
        if (!StartsWithAny(path, _media))
        {
            return false;
        }

        if (!path.StartsWithSegments("/Items", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = Segments(path);
        return segments.Length >= 3
            && (segments[2].Equals("Download", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("File", StringComparison.OrdinalIgnoreCase));
    }

    private static PathString Trim(PathString path)
    {
        var value = path.Value ?? string.Empty;
        var trimmed = value.TrimEnd('/');
        return trimmed.Length == value.Length ? path : new PathString(trimmed.Length == 0 ? "/" : trimmed);
    }

    private static bool StartsWithAny(PathString path, IEnumerable<string> prefixes)
        => prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private static string[] Segments(PathString path)
        => (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
