using System;
using System.Net.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Builds the HTTP clients YARP forwards to Jellyfin with, with a short connect timeout.
/// </summary>
public sealed class ProxyHttpClientFactory : ForwarderHttpClientFactory
{
    /// <inheritdoc />
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        base.ConfigureHandler(context, handler);
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);
    }
}
