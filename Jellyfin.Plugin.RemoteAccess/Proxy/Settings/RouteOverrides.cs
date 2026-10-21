namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Per-route values that replace the global ones. A null value inherits the global setting. Connection-wide
/// settings (ports, keep-alive, header limits, connection caps, HTTP/2) cannot be set per route, because they
/// apply before the proxy knows which route a request is for.
/// </summary>
public class RouteOverrides
{
    /// <summary>Gets or sets the largest upload from the internet, in MB (zero is unlimited). Null inherits.</summary>
    public int? MaxRequestBodyMbWan { get; set; }

    /// <summary>Gets or sets the largest upload from the LAN, in MB (zero is unlimited). Null inherits.</summary>
    public int? MaxRequestBodyMbLan { get; set; }

    /// <summary>Gets or sets how long the service may go silent, in seconds. Null inherits.</summary>
    public int? BackendActivityTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the minimum rate, in bytes per second, a client must read at. Null leaves it off, because
    /// the proxy cannot tell whether another service is streaming.
    /// </summary>
    public int? MinResponseBytesPerSecond { get; set; }

    /// <summary>Gets or sets a value indicating whether text responses are compressed. Null means on.</summary>
    public bool? Compress { get; set; }

    /// <summary>Gets or sets a value indicating whether HSTS is sent. Null inherits.</summary>
    public bool? HstsEnabled { get; set; }

    /// <summary>Gets or sets the HSTS max-age in days. Null inherits.</summary>
    public int? HstsMaxAgeDays { get; set; }

    /// <summary>Gets or sets a value indicating whether pages may only be framed by the same origin. Null inherits.</summary>
    public bool? SameOriginFramesOnly { get; set; }
}
