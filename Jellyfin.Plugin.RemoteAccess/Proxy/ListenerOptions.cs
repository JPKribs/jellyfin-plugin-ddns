namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Everything that needs the proxy host rebuilt to change: ports, protocols and server-wide limits. Any other
/// setting applies to the running host. A record so a change is a simple inequality.
/// </summary>
/// <param name="HttpPort">The HTTP port. Zero picks a free port.</param>
/// <param name="HttpsPort">The HTTPS port, or null when no certificate is configured.</param>
/// <param name="LoopbackOnly">Whether the listeners bind to loopback only (probes and tests).</param>
/// <param name="Http2">Whether HTTP/2 is offered over TLS.</param>
/// <param name="KeepAliveSeconds">The keep-alive timeout.</param>
/// <param name="HeadersTimeoutSeconds">The request headers timeout.</param>
/// <param name="MaxHeadersKb">The request header size limit.</param>
/// <param name="MaxConnections">The global connection limit, zero for none.</param>
public sealed record ListenerOptions(
    int HttpPort,
    int? HttpsPort,
    bool LoopbackOnly,
    bool Http2,
    int KeepAliveSeconds,
    int HeadersTimeoutSeconds,
    int MaxHeadersKb,
    int MaxConnections);
