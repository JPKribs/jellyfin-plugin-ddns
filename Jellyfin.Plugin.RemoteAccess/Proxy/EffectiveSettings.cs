using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Resolves the value that applies to one request from the global settings.
/// </summary>
public static class EffectiveSettings
{
    /// <summary>
    /// The largest request body accepted, in MB, or zero for unlimited.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="local">Whether the client is local.</param>
    /// <returns>The limit in MB.</returns>
    public static int MaxRequestBodyMb(ProxyRuntime runtime, bool local)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return local ? runtime.Options.MaxRequestBodyMbLan : runtime.Options.MaxRequestBodyMbWan;
    }

    /// <summary>
    /// How long the backend may go silent, in seconds.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The timeout in seconds.</returns>
    public static int BackendActivityTimeoutSeconds(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Options.BackendActivityTimeoutSeconds;
    }

    /// <summary>
    /// The minimum response read rate in bytes per second, or null when it does not apply. Media never has one,
    /// since a paused player stops reading.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The rate, or null for none.</returns>
    public static int? MinResponseBytesPerSecond(ProxyRuntime runtime, PathString path)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var rate = PathRules.IsMedia(PathRules.StripBase(path, runtime.BaseUrl)) ? 0 : runtime.Options.MinResponseBytesPerSecond;
        return rate > 0 ? rate : null;
    }

    /// <summary>
    /// The HSTS max-age in days, or null when HSTS is off.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The max-age in days, or null.</returns>
    public static int? HstsMaxAgeDays(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Options.HstsEnabled ? runtime.Options.HstsMaxAgeDays : null;
    }

    /// <summary>
    /// Whether pages may only be framed by the same origin.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>True for same-origin framing only.</returns>
    public static bool SameOriginFramesOnly(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Options.SameOriginFramesOnly;
    }
}
