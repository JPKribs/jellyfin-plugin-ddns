using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// The first stop for every request: classifies the client, applies <see cref="RequestPolicy"/>, sets
/// per-request limits, tags the request with its route, and adds security headers to the response.
/// </summary>
public sealed class ProxyPolicyMiddleware
{
    /// <summary>
    /// The internal header that tells YARP which route a request belongs to. Any copy sent by a client is
    /// removed first, and the transforms remove it again before forwarding.
    /// </summary>
    public const string RouteHeader = "X-Ddns-Proxy-Route";

    /// <summary>
    /// Added to every request forwarded to a Service, as this process's value and a hop count. It coming back in
    /// means a Service leads back to this proxy. A few such hops are a legitimate chain, such as a Service that is
    /// itself a proxy forwarding to another name here; past that it is a loop, which would use every connection.
    /// </summary>
    public const string HopHeader = "X-Remote-Access-Hop";

    /// <summary>How many times a request may come back through this proxy before it is refused as a loop.</summary>
    public const int MaxHops = 3;

    /// <summary>
    /// Gets this process's value for <see cref="HopHeader"/>. Another server running this plugin uses its own, so a
    /// Service pointing at another instance still works.
    /// </summary>
    public static string HopValue { get; } = Guid.NewGuid().ToString("N");

    private const string RouteItem = "ddns.proxy.route";

    // The most a sign-in request may send, and how long it may take to send it.
    private const int SignInBodyLimit = 16 * 1024;
    private static readonly TimeSpan _signInReadLimit = TimeSpan.FromSeconds(10);

    private readonly RequestDelegate _next;
    private readonly ProxyRuntimeHolder _holder;
    private readonly Http01ChallengeStore _challenges;
    private readonly IAccessRecorder _recorder;
    private readonly IFailureTracker _failures;
    private readonly ConnectionGate _gate;
    private readonly LocalTrafficWatch _localTraffic;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyPolicyMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="holder">The runtime holder.</param>
    /// <param name="challenges">Pending HTTP-01 challenges.</param>
    /// <param name="recorder">Where the address each client used is reported.</param>
    /// <param name="failures">Where failed sign-ins from the internet are reported, for bans.</param>
    /// <param name="gate">The connection gate, whose block list, ban and country filters every request passes.</param>
    /// <param name="localTraffic">Notices when every visitor looks local.</param>
    public ProxyPolicyMiddleware(RequestDelegate next, ProxyRuntimeHolder holder, Http01ChallengeStore challenges, IAccessRecorder recorder, IFailureTracker failures, ConnectionGate gate, LocalTrafficWatch localTraffic)
    {
        _localTraffic = localTraffic;
        _gate = gate;
        _failures = failures;
        _next = next;
        _holder = holder;
        _challenges = challenges;
        _recorder = recorder;
    }

    /// <summary>
    /// The address a request used, like <c>https://media.example.com:8920</c>, for the history.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The scheme, host and port, lower-cased.</returns>
    public static string AccessUrl(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Rebuilt from the parsed name and port rather than the raw header, which a client can pad to tens of
        // kilobytes; this string is kept in memory and in history.
        var name = request.Host.Host;
        if (name.Length > 253)
        {
            name = name[..253];
        }

        var port = request.Host.Port is { } p and > 0 and <= 65535 ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return (request.Scheme + "://" + name + port).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the external route a request was matched to, or null for Jellyfin.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The route.</returns>
    public static RuntimeRoute? RouteOf(HttpContext context)
        => context?.Items.TryGetValue(RouteItem, out var value) == true ? value as RuntimeRoute : null;

    /// <summary>
    /// Whether the proxy should compress this response: Jellyfin's when that option is on (Jellyfin does not
    /// compress responses it sees as HTTPS), a route's when its own option is on. Range requests never are.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="runtime">The runtime.</param>
    /// <returns>True to compress.</returns>
    public static bool ShouldCompress(HttpContext context, ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtime);
        if (context.Request.Headers.ContainsKey(HeaderNames.Range))
        {
            return false;
        }

        var route = RouteOf(context);
        // A Service is compressed only when asked: compressing pages that mix a secret with reflected input over
        // HTTPS can leak the secret (BREACH), and Jellyfin, which uses header tokens, is the one known to be safe.
        return route is null ? runtime.Options.CompressJellyfin : route.Overrides.Compress ?? false;
    }

    /// <summary>
    /// Runs the middleware.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runtime = _holder.Current;
        var request = context.Request;
        var hops = HopsOf(request.Headers[HopHeader].ToString());
        if (hops >= MaxHops)
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            return;
        }

        var client = context.Connection.RemoteIpAddress;
        var clientAddress = client is null ? null : NetworkClassifier.Normalize(client);
        var local = runtime.Classifier.IsLocal(client);
        var host = HostnameMatcher.Normalize(request.Host.Host);
        var publicName = HostnameMatcher.IsPublicName(host) && runtime.IsConfiguredHost(host);
        _localTraffic.Observe(local, publicName, client, DateTime.UtcNow);
        var facts = new RequestFacts(local, request.IsHttps, request.Host.Host, request.Method, request.Path, request.QueryString);
        var decision = RequestPolicy.Decide(runtime, facts, _challenges);

        // The connection was checked when it opened, but a client blocked or banned since then must not keep
        // using it, and plain HTTP is only checked here. Certificate checks always get through.
        if (!local && clientAddress is { } address && decision.Action != RequestAction.Challenge
            && !_gate.Filters.All(f => f.Allow(address, runtime)))
        {
            context.Abort();
            return;
        }

        switch (decision.Action)
        {
            case RequestAction.Drop:
                context.Abort();
                return;
            case RequestAction.Forbidden:
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            case RequestAction.Redirect:
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = decision.Value;
                return;
            case RequestAction.Challenge:
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(decision.Value, context.RequestAborted).ConfigureAwait(false);
                return;
        }

        var route = decision.Route;

        // Jellyfin is never reached through one of this proxy's own Services. A Service pointed back at the proxy
        // would hand Jellyfin an internet visitor from this machine's address, past every internet rule.
        if (route is null && hops > 0)
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            return;
        }

        var path = PathRules.StripBase(request.Path, runtime.BaseUrl);
        context.Items[RouteItem] = route;
        request.Headers.Remove(RouteHeader);
        request.Headers[RouteHeader] = route?.Id ?? (PathRules.IsMedia(path) ? ProxyRuntime.JellyfinMediaId : ProxyRuntime.JellyfinId);

        // An upload that declares a size over the limit is refused before the backend hears about it; without
        // a declared size the limit still stops the body mid-stream.
        var limitMb = EffectiveSettings.MaxRequestBodyMb(runtime, route, local);
        if (limitMb > 0 && request.ContentLength > limitMb * 1024L * 1024L)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        ApplyLimits(context, runtime, route, local);
        context.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(context, runtime, route);
            return Task.CompletedTask;
        });

        if (route is null && clientAddress is not null)
        {
            _recorder.RecordRequest(clientAddress, AccessUrl(request), request.Headers.UserAgent.ToString(), local);
        }

        var signIn = route is null && clientAddress is not null && PathRules.IsSignIn(path);
        var internetSignIn = signIn && !local;
        if (internetSignIn && !await ReadSignInBodyAsync(context).ConfigureAwait(false))
        {
            return;
        }

        if (internetSignIn && !_gate.TryEnterSignIn(clientAddress!))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            if (internetSignIn)
            {
                _gate.ExitSignIn(clientAddress!);
            }
        }

        if (signIn && IsFailedSignIn(context.Response.StatusCode, path))
        {
            _recorder.RecordSignInFailure(clientAddress!, AccessUrl(request));

            // The ban takes effect at once: every connection from the address is closed, streams included.
            if (!local && _failures.RecordFailure(clientAddress!, runtime.Options))
            {
                context.Abort();
                _gate.Recheck();
            }
        }
    }

    /// <summary>
    /// How many times a request has already passed through this proxy, from its <see cref="HopHeader"/>.
    /// </summary>
    /// <param name="header">The header's value, which other instances may have added to.</param>
    /// <returns>The count; zero when this proxy has not seen it.</returns>
    public static int HopsOf(string? header)
    {
        foreach (var part in (header ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf(':', StringComparison.Ordinal);
            if (split > 0 && string.Equals(part[..split], HopValue, StringComparison.Ordinal)
                && int.TryParse(part[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var hops))
            {
                return hops;
            }
        }

        return 0;
    }

    /// <summary>
    /// The <see cref="HopHeader"/> value to send on: other instances' entries kept, this one's count raised.
    /// </summary>
    /// <param name="header">The value the request came in with.</param>
    /// <returns>The value to forward.</returns>
    public static string NextHop(string? header)
    {
        var others = (header ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(HopValue + ":", StringComparison.Ordinal));
        return string.Join(", ", others.Append(HopValue + ":" + (HopsOf(header) + 1).ToString(CultureInfo.InvariantCulture)));
    }

    // A sign-in is a few hundred bytes. It is read in full, quickly, before it takes a sign-in slot, so a client
    // trickling a large body cannot hold the slots every other user behind the same address needs.
    private static async Task<bool> ReadSignInBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > SignInBodyLimit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }

        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false })
        {
            size.MaxRequestBodySize = SignInBodyLimit;
        }

        request.EnableBuffering(SignInBodyLimit, SignInBodyLimit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(_signInReadLimit);
        try
        {
            await request.Body.CopyToAsync(Stream.Null, deadline.Token).ConfigureAwait(false);
            request.Body.Position = 0;
            return true;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            return false;
        }
        catch (Exception ex) when (ex is Microsoft.AspNetCore.Http.BadHttpRequestException or IOException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }
    }

    // Jellyfin answers a wrong password with 401, and a wrong Quick Connect secret with 404.
    private static bool IsFailedSignIn(int status, PathString path)
        => status == StatusCodes.Status401Unauthorized
            || (status == StatusCodes.Status404NotFound && PathRules.IsQuickConnectSignIn(path));

    private static void ApplyLimits(HttpContext context, ProxyRuntime runtime, RuntimeRoute? route, bool local)
    {
        var bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false })
        {
            var mb = EffectiveSettings.MaxRequestBodyMb(runtime, route, local);
            bodyFeature.MaxRequestBodySize = mb <= 0 ? null : mb * 1024L * 1024L;
        }

        // Kestrel only lets HTTP/1.1 requests set a response rate. HTTP/2 would take one server-wide value for every
        // stream, media included, and a paused player stops reading, so HTTP/2 has none; the idle timers and the
        // per-address connection cap bound a slow reader there instead.
        if (!HttpProtocol.IsHttp11(context.Request.Protocol))
        {
            return;
        }

        var rateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();
        if (rateFeature is not null)
        {
            var rate = EffectiveSettings.MinResponseBytesPerSecond(runtime, route, context.Request.Path);
            rateFeature.MinDataRate = rate is { } bytes ? new MinDataRate(bytes, TimeSpan.FromSeconds(5)) : null;
        }
    }

    private static void ApplySecurityHeaders(HttpContext context, ProxyRuntime runtime, RuntimeRoute? route)
    {
        var headers = context.Response.Headers;
        headers.Remove(HeaderNames.Server);
        headers.Remove("X-Powered-By");

        if (!headers.ContainsKey(HeaderNames.XContentTypeOptions))
        {
            headers.XContentTypeOptions = "nosniff";
        }

        if (!headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = "same-origin";
        }

        if (EffectiveSettings.SameOriginFramesOnly(runtime, route)
            && !headers.ContainsKey(HeaderNames.XFrameOptions)
            && !headers.ContentSecurityPolicy.ToString().Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase))
        {
            headers.XFrameOptions = "SAMEORIGIN";
        }

        // HSTS only on HTTPS and only for configured names: sending it for a bare IP or a LAN name would pin
        // HTTPS onto names that have no valid certificate.
        if (EffectiveSettings.HstsMaxAgeDays(runtime, route) is { } days
            && context.Request.IsHttps
            && runtime.IsConfiguredHost(context.Request.Host.Host)
            && !headers.ContainsKey(HeaderNames.StrictTransportSecurity))
        {
            var seconds = days * 86400L;
            headers.StrictTransportSecurity = "max-age=" + seconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
