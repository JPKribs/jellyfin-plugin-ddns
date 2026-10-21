using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Builds the HTTP clients YARP forwards with: short connect timeout, and certificate pinning for routes
/// that ask for it.
/// </summary>
public sealed class ProxyHttpClientFactory : ForwarderHttpClientFactory
{
    private readonly ProxyRuntimeHolder _holder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyHttpClientFactory"/> class.
    /// </summary>
    /// <param name="holder">The runtime holder, for the ports no Service may reach on this machine.</param>
    public ProxyHttpClientFactory(ProxyRuntimeHolder holder)
    {
        _holder = holder;
    }

    /// <inheritdoc />
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        base.ConfigureHandler(context, handler);
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);

        // The target's name is checked against the address it resolves to now, not only when it was saved, so
        // neither a name pointing at a cloud metadata service nor DNS rebinding reaches one. A Service is also
        // refused this machine on Jellyfin's or the proxy's ports, whatever name brought it there.
        var service = context.ClusterId is not (ProxyRuntime.JellyfinId or ProxyRuntime.JellyfinMediaId);
        handler.ConnectCallback = (connect, token) => ConnectAsync(connect, service, token);

        var pin = PinOf(context.NewMetadata);
        if (pin.Length > 0)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && string.Equals(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())), pin, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    protected override bool CanReuseOldClient(ForwarderHttpClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The pin lives in metadata, which the base comparison ignores, so a changed pin needs a new client.
        return base.CanReuseOldClient(context)
            && string.Equals(PinOf(context.OldMetadata), PinOf(context.NewMetadata), StringComparison.Ordinal);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool service, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        var reserved = service && _holder.Current.ReservedPorts.Contains(endpoint.Port);
        var allowed = addresses
            .Where(a => !ProxySettingsValidator.IsMetadataAddress(a))
            .Where(a => !reserved || !SelfTarget.IsThisMachine(a))
            .ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(reserved
                ? $"{endpoint.Host} is this server on Jellyfin's or the proxy's port, which a Service may not reach."
                : $"{endpoint.Host} resolves to a cloud metadata or link-local address, which a Service may not reach.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string PinOf(IReadOnlyDictionary<string, string>? metadata)
        => metadata is not null && metadata.TryGetValue(ProxyConfigProvider.PinnedThumbprintKey, out var pin) ? pin : string.Empty;
}
