using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Resolves the value that applies to one request: the route's override when it has one, otherwise the
/// global setting. Jellyfin itself (a null route) always uses the global settings.
/// </summary>
public static class EffectiveSettings
{
    /// <summary>
    /// The largest request body accepted, in MB, or zero for unlimited.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="route">The external route, or null for Jellyfin.</param>
    /// <param name="local">Whether the client is local.</param>
    /// <returns>The limit in MB.</returns>
    public static int MaxRequestBodyMb(ProxyRuntime runtime, RuntimeRoute? route, bool local)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var global = local ? runtime.Options.MaxRequestBodyMbLan : runtime.Options.MaxRequestBodyMbWan;
        var own = local ? route?.Overrides.MaxRequestBodyMbLan : route?.Overrides.MaxRequestBodyMbWan;
        return own ?? global;
    }

    /// <summary>
    /// How long the backend may go silent, in seconds.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="route">The external route, or null for Jellyfin.</param>
    /// <returns>The timeout in seconds.</returns>
    public static int BackendActivityTimeoutSeconds(ProxyRuntime runtime, RuntimeRoute? route)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return route?.Overrides.BackendActivityTimeoutSeconds ?? runtime.Options.BackendActivityTimeoutSeconds;
    }

    /// <summary>
    /// The minimum response read rate in bytes per second, or null when it does not apply. Jellyfin uses the
    /// global value except for media; a route uses only its own value, since its streams cannot be recognized.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="route">The external route, or null for Jellyfin.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The rate, or null for none.</returns>
    public static int? MinResponseBytesPerSecond(ProxyRuntime runtime, RuntimeRoute? route, PathString path)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var rate = route is null
            ? (PathRules.IsMedia(PathRules.StripBase(path, runtime.BaseUrl)) ? 0 : runtime.Options.MinResponseBytesPerSecond)
            : route.Overrides.MinResponseBytesPerSecond ?? 0;
        return rate > 0 ? rate : null;
    }

    /// <summary>
    /// The HSTS max-age in days, or null when HSTS is off.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="route">The external route, or null for Jellyfin.</param>
    /// <returns>The max-age in days, or null.</returns>
    public static int? HstsMaxAgeDays(ProxyRuntime runtime, RuntimeRoute? route)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var enabled = route?.Overrides.HstsEnabled ?? runtime.Options.HstsEnabled;
        return enabled ? route?.Overrides.HstsMaxAgeDays ?? runtime.Options.HstsMaxAgeDays : null;
    }

    /// <summary>
    /// Whether pages may only be framed by the same origin.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="route">The external route, or null for Jellyfin.</param>
    /// <returns>True for same-origin framing only.</returns>
    public static bool SameOriginFramesOnly(ProxyRuntime runtime, RuntimeRoute? route)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return route?.Overrides.SameOriginFramesOnly ?? runtime.Options.SameOriginFramesOnly;
    }
}
