using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// The reverse proxy: a separate ASP.NET Core host with its own Kestrel server and service container,
/// running inside the Jellyfin process. Built once per set of <see cref="ListenerOptions"/>; everything else
/// changes live through the runtime holder.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private static readonly string[] _extraCompressedTypes = { "image/svg+xml", "application/manifest+json" };

    private readonly WebApplication _app;
    private readonly ProxyConfigProvider _config;
    private readonly ConnectionGate _gate;

    private ProxyHost(WebApplication app, ProxyConfigProvider config, ConnectionGate gate, ListenerOptions listener, IReadOnlyList<string> addresses)
    {
        _app = app;
        _gate = gate;
        _config = config;
        Listener = listener;
        Addresses = addresses;
    }

    /// <summary>Gets the listener options the host was built with.</summary>
    public ListenerOptions Listener { get; }

    /// <summary>Gets the addresses the host is listening on.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>
    /// Builds and starts a proxy host. Throws when a port cannot be bound.
    /// </summary>
    /// <param name="options">The host's inputs.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running host.</returns>
    public static async Task<ProxyHost> StartAsync(ProxyHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var holder = options.Holder;
        var runtime = holder.Current;
        var listener = runtime.Listener;
        var config = new ProxyConfigProvider(runtime);
        var gate = new ConnectionGate(holder, options.ConnectionFilters);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(ProxyHost).Assembly.GetName().Name,
            ContentRootPath = options.ContentRoot,
            EnvironmentName = Environments.Production
        });

        // The host must not pick anything up from the environment Jellyfin runs in (ASPNETCORE_URLS,
        // Kestrel sections and so on). Everything it does is set below.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RedactingLoggerProvider(options.LoggerFactory));
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // At Information these categories log full request and destination URLs, query strings included.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("Yarp", LogLevel.Warning);

        builder.Services.AddSingleton<IHostLifetime, DetachedHostLifetime>();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(gate);
        builder.Services.AddSingleton(options.Challenges);
        builder.Services.AddSingleton(options.AccessRecorder ?? NullAccessRecorder.Instance);
        builder.Services.AddSingleton(options.FailureTracker ?? NullFailureTracker.Instance);
        builder.Services.AddSingleton(options.LocalTraffic);
        builder.Services.AddSingleton<IProxyConfigProvider>(config);
        builder.Services.AddSingleton<IForwarderHttpClientFactory, ProxyHttpClientFactory>();
        builder.Services.AddReverseProxy().AddTransforms(ConfigureTransforms);
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(_extraCompressedTypes);
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k => ConfigureKestrel(k, listener, gate, options.Certificates, holder));

        var app = builder.Build();
        app.UseMiddleware<ProxyPolicyMiddleware>();
        app.UseRouting();
        app.UseWhen(ctx => ProxyPolicyMiddleware.ShouldCompress(ctx, holder.Current), b => b.UseResponseCompression());
        app.MapReverseProxy();

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList()
            ?? new List<string>();
        return new ProxyHost(app, config, gate, listener, addresses);
    }

    /// <summary>
    /// Pushes a new runtime's routes and clusters into the running host. The caller has already swapped the
    /// holder; listener changes need a new host instead.
    /// </summary>
    /// <param name="runtime">The new runtime.</param>
    public void Update(ProxyRuntime runtime) => _config.Update(runtime);

    /// <summary>
    /// Stops the host and releases its ports. Jellyfin restarts inside the same process, so the ports must
    /// be free before the next instance binds them.
    /// </summary>
    /// <param name="cancellationToken">Bounds how long open requests get to finish.</param>
    /// <returns>A task.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timed out; disposing below still closes the listeners.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static readonly string[] _clientClaims =
    {
        "X-Forwarded-Prefix", "X-Forwarded-Port", "X-Forwarded-Ssl", "X-Forwarded-Scheme", "X-Forwarded-Server",
        "X-Forwarded-Protocol", "X-Original-URL", "X-Rewrite-URL", "X-Original-Forwarded-For", "X-Client-IP",
        "X-Cluster-Client-IP", "True-Client-IP", "CF-Connecting-IP", "Fastly-Client-IP", "X-ProxyUser-Ip"
    };

    private static void ConfigureTransforms(Yarp.ReverseProxy.Transforms.Builder.TransformBuilderContext context)
    {
        // Our own forwarding headers replace anything the client sent, so a client cannot claim a LAN address.
        context.UseDefaultForwarders = false;
        context.AddXForwardedFor(action: ForwardedTransformActions.Set);
        context.AddXForwardedProto(action: ForwardedTransformActions.Set);
        context.AddXForwardedHost(action: ForwardedTransformActions.Set);
        context.AddRequestHeaderRemove("Forwarded");

        // Other headers apps read for the client's address or scheme. A client could set any of them, and an app
        // that trusts this proxy would believe it, so none is passed on.
        foreach (var header in _clientClaims)
        {
            context.AddRequestHeaderRemove(header);
        }
        context.AddRequestHeaderRemove(ProxyPolicyMiddleware.RouteHeader);
        // Services only: Jellyfin is never a hop back to this proxy, so it never sees the marker. No response
        // carries it back out, so a backend that echoes headers cannot show it to clients.
        if (context.Route.ClusterId is not (ProxyRuntime.JellyfinId or ProxyRuntime.JellyfinMediaId))
        {
            context.AddRequestTransform(t =>
            {
                var incoming = t.HttpContext.Request.Headers[ProxyPolicyMiddleware.HopHeader].ToString();
                t.ProxyRequest.Headers.Remove(ProxyPolicyMiddleware.HopHeader);
                t.ProxyRequest.Headers.TryAddWithoutValidation(ProxyPolicyMiddleware.HopHeader, ProxyPolicyMiddleware.NextHop(incoming));
                return ValueTask.CompletedTask;
            });
        }
        else
        {
            context.AddRequestHeaderRemove(ProxyPolicyMiddleware.HopHeader);
        }

        context.AddResponseHeaderRemove(ProxyPolicyMiddleware.HopHeader, ResponseCondition.Always);
        context.AddRequestTransform(t =>
        {
            var headers = t.ProxyRequest.Headers;
            headers.Remove("X-Real-IP");
            var ip = t.HttpContext.Connection.RemoteIpAddress;
            if (ip is not null)
            {
                headers.TryAddWithoutValidation("X-Real-IP", NetworkClassifier.Normalize(ip).ToString());
            }

            return ValueTask.CompletedTask;
        });
        context.AddResponseHeaderRemove("Server", ResponseCondition.Always);
        context.AddResponseHeaderRemove("X-Powered-By", ResponseCondition.Always);

        if (context.Route.Metadata?.TryGetValue(ProxyConfigProvider.PreserveHostKey, out var preserve) == true
            && string.Equals(preserve, "true", StringComparison.Ordinal))
        {
            context.AddOriginalHost(true);
        }
    }

    private static void ConfigureKestrel(
        KestrelServerOptions kestrel,
        ListenerOptions listener,
        ConnectionGate gate,
        CertificateStore certificates,
        ProxyRuntimeHolder holder)
    {
        kestrel.AddServerHeader = false;
        var limits = kestrel.Limits;
        limits.KeepAliveTimeout = TimeSpan.FromSeconds(listener.KeepAliveSeconds);
        limits.RequestHeadersTimeout = TimeSpan.FromSeconds(listener.HeadersTimeoutSeconds);
        limits.MaxRequestHeadersTotalSize = listener.MaxHeadersKb * 1024;
        // The connection limit is applied by the gate to internet connections only, so the internet cannot use
        // it up and lock out the local network.
        limits.MaxConcurrentConnections = null;
        limits.MaxConcurrentUpgradedConnections = null;

        // Both are set per request by the policy middleware: body size by LAN or internet, response rate off for media.
        limits.MaxRequestBodySize = null;
        limits.MinResponseDataRate = null;

        void Gate(ListenOptions o, bool countries) => o.Use(next => connection => gate.OnConnectionAsync(connection, next, countries));

        Listen(kestrel, listener.HttpPort, listener.LoopbackOnly, o =>
        {
            o.Protocols = HttpProtocols.Http1;
            Gate(o, countries: false);
        });

        if (listener.HttpsPort is not { } httpsPort)
        {
            return;
        }

        Listen(kestrel, httpsPort, listener.LoopbackOnly, o =>
        {
            o.Protocols = listener.Http2 ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http1;
            Gate(o, countries: true);
            o.Use(next => connection => PlainHttpRedirect.OnConnectionAsync(connection, next, holder));
            var alpn = listener.Http2
                ? new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 }
                : new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };
            o.UseHttps(new TlsHandshakeCallbackOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                OnConnection = ctx =>
                {
                    var remote = (ctx.Connection.RemoteEndPoint as IPEndPoint)?.Address;
                    var local = holder.Current.Classifier.IsLocal(remote);
                    var certificate = certificates.Select(ctx.ClientHelloInfo.ServerName, local)
                        ?? throw new AuthenticationException("No certificate for the requested name.");
                    return ValueTask.FromResult(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = certificate,
#pragma warning disable CA5398 // Pinned on purpose: "None" lets some older operating systems still offer TLS 1.0 and 1.1.
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
                        ApplicationProtocols = alpn,
                        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                    });
                }
            });
        });
    }

    private static void Listen(KestrelServerOptions kestrel, int port, bool loopbackOnly, Action<ListenOptions> configure)
    {
        if (loopbackOnly)
        {
            kestrel.Listen(IPAddress.Loopback, port, configure);
        }
        else
        {
            kestrel.ListenAnyIP(port, configure);
        }
    }
}
