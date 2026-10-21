using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Runs the real proxy host on loopback in front of a fake Jellyfin and a fake second service. Loopback is
/// classified as the internet here (unless a test says otherwise), so every internet rule is exercised over
/// real sockets and real TLS.
/// </summary>
public sealed class ProxyHostTests : IAsyncLifetime
{
    private const string Host = "media.example.com";
    private const string RouteHost = "requests.example.com";

    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-proxy-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerFactory _logs = new();
    private WebApplication? _jellyfin;
    private WebApplication? _service;
    private Uri _jellyfinUri = null!;
    private Uri _serviceUri = null!;
    private int _webAssetHits;
    private int _uploads;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        (_jellyfin, _jellyfinUri) = await StartBackendAsync(app =>
        {
            app.MapGet("/System/Info/Public", () => Results.Json(new { Id = "abc" }));
            app.MapGet("/Users/Public", () => Results.Json(new[] { new { Name = "alice" } }));
            app.MapPost("/Users/AuthenticateByName", () => Results.Unauthorized());
            app.MapGet("/web/main.js", () =>
            {
                Interlocked.Increment(ref _webAssetHits);
                return Results.Text("console.log('x');".PadRight(4000, ' '), "application/javascript");
            });
            app.Map("/{**rest}", (HttpContext ctx) => Results.Json(Echo(ctx, "jellyfin")));
        }).ConfigureAwait(false);
        (_service, _serviceUri) = await StartBackendAsync(app =>
        {
            app.MapPost("/upload", async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref _uploads);
                using var sink = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(sink).ConfigureAwait(false);
                return Results.Text(sink.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
            app.Map("/{**rest}", (HttpContext ctx) => Results.Json(Echo(ctx, "service")));
        }).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_jellyfin is not null)
        {
            await _jellyfin.DisposeAsync().ConfigureAwait(false);
        }

        if (_service is not null)
        {
            await _service.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Wan_Https_ForwardsWithOurOwnForwardingHeaders()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}:{proxy.HttpsPort}/Items?x=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.9.9.9");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "10.9.9.9");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=10.9.9.9");
        request.Headers.TryAddWithoutValidation(ProxyPolicyMiddleware.RouteHeader, "req");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var echo = await ReadEcho(response).ConfigureAwait(false);

        Assert.Equal("jellyfin", echo["backend"]);
        Assert.Equal("127.0.0.1", echo["x-forwarded-for"]);
        Assert.Equal("127.0.0.1", echo["x-real-ip"]);
        Assert.Equal("https", echo["x-forwarded-proto"]);
        Assert.Equal($"{Host}:{proxy.HttpsPort}", echo["host"]);
        Assert.Equal(string.Empty, echo["forwarded"]);
        Assert.Equal(string.Empty, echo["route-header"]);
        Assert.False(response.Headers.Contains("Server"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wan_UnknownSni_GetsNoCertificate()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"https://unknown.example.org:{proxy.HttpsPort}/"))).ConfigureAwait(false);
    }

    [Fact]
    public async Task Wan_KnownSniUnknownHost_IsDropped()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}:{proxy.HttpsPort}/");
        request.Headers.Host = "other.example.org";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request)).ConfigureAwait(false);
    }

    [Fact]
    public async Task Wan_PlainHttp_RedirectsAConfiguredNameAndDropsTheRest()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpPort, followRedirects: false);
        using var redirect = await client.GetAsync(new Uri($"http://{Host}:{proxy.HttpPort}/web/index.html")).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal(new Uri($"https://{Host}/web/index.html"), redirect.Headers.Location);

        // A name the proxy does not serve gets no answer at all.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{Host}:{proxy.HttpPort}/"));
        request.Headers.Host = "other.example.org";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request)).ConfigureAwait(false);
    }

    [Fact]
    public async Task Block_CutsOffAClientOnItsOpenConnection()
    {
        // The connection is allowed when it opens; the client is blocked afterwards, as a ban or a Dashboard
        // block would. Its next request on the same kept-alive connection must not reach Jellyfin.
        var gate = new SwitchFilter();
        await using var proxy = await StartProxyAsync(filter: gate).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        var uri = new Uri($"https://{Host}:{proxy.HttpsPort}/System/Info/Public");
        await client.GetStringAsync(uri).ConfigureAwait(false);

        gate.Refuse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(uri)).ConfigureAwait(false);
    }

    [Fact]
    public async Task PlainHttpOnHttpsPort_LocalGetsRedirect()
    {
        await using var proxy = await StartProxyAsync(loopbackIsLocal: true).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort, followRedirects: false);
        using var response = await client.GetAsync(new Uri($"http://localhost:{proxy.HttpsPort}/web/index.html?x=1")).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(new Uri($"https://localhost:{proxy.HttpsPort}/web/index.html?x=1"), response.Headers.Location);

        // Following it lands on the real page over HTTPS.
        using var follow = Client(proxy.HttpsPort);
        using var landed = await follow.GetAsync(new Uri($"http://localhost:{proxy.HttpsPort}/System/Info/Public")).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(Uri.UriSchemeHttps, landed.RequestMessage!.RequestUri!.Scheme);
    }

    [Fact]
    public async Task PlainHttpOnHttpsPort_InternetIsDropped()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort, followRedirects: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://{Host}:{proxy.HttpsPort}/"))).ConfigureAwait(false);
    }

    [Fact]
    public async Task Wan_UserListEmpty_StartupForbidden()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);

        var users = await client.GetStringAsync(new Uri($"https://{Host}:{proxy.HttpsPort}/Users/Public")).ConfigureAwait(false);
        Assert.Equal("[]", users);

        using var startup = await client.GetAsync(new Uri($"https://{Host}:{proxy.HttpsPort}/Startup/Configuration")).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Forbidden, startup.StatusCode);
    }

    [Fact]
    public async Task Lan_SeesUserListOverPlainHttp()
    {
        await using var proxy = await StartProxyAsync(loopbackIsLocal: true).ConfigureAwait(false);
        using var client = Client(proxy.HttpPort);
        var users = await client.GetStringAsync(new Uri($"http://127.0.0.1:{proxy.HttpPort}/Users/Public")).ConfigureAwait(false);
        Assert.Contains("alice", users, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Routes_GoToTheirServiceAndRespectLanOnly()
    {
        await using var proxy = await StartProxyAsync(edit: s =>
        {
            s.Routes.Add(new ProxyRoute { Id = "req", Hostnames = new List<string> { RouteHost }, Target = _serviceUri.ToString() });
            s.Routes.Add(new ProxyRoute { Id = "lan", Hostnames = new List<string> { "lan.example.com" }, Target = _serviceUri.ToString(), Exposure = RouteExposure.LanOnly });
        }).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);

        using var response = await client.GetAsync(new Uri($"https://{RouteHost}:{proxy.HttpsPort}/api/v1/status")).ConfigureAwait(false);
        var echo = await ReadEcho(response).ConfigureAwait(false);
        Assert.Equal("service", echo["backend"]);
        Assert.Equal("/api/v1/status", echo["path"]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"https://lan.example.com:{proxy.HttpsPort}/"))).ConfigureAwait(false);
    }

    [Fact]
    public async Task Recorder_GetsTheAddressUsedAndRefusedSignIns()
    {
        var recorder = new Jellyfin.Plugin.DynamicDns.People.AccessRecorder();
        await using var proxy = await StartProxyAsync(recorder: recorder).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);

        await client.GetStringAsync(new Uri($"https://{Host}:{proxy.HttpsPort}/System/Info/Public")).ConfigureAwait(false);
        Assert.Equal($"https://{Host}:{proxy.HttpsPort}", recorder.UrlFor("127.0.0.1"));

        // Loopback counts as the internet in this test, so the visit is buffered for the history.
        Assert.Equal("127.0.0.1", Assert.Single(recorder.DrainSeen()).Ip);

        using var signIn = await client.PostAsync(new Uri($"https://{Host}:{proxy.HttpsPort}/Users/AuthenticateByName"), new StringContent("{}")).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(new[] { "127.0.0.1" }, recorder.TakeUnclaimed(TimeSpan.Zero).Select(f => f.Ip));
    }

    [Fact]
    public async Task SignIn_LargeOrSlowBody_RefusedBeforeTakingASlot()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        var url = new Uri($"https://{Host}:{proxy.HttpsPort}/Users/AuthenticateByName");

        // A sign-in body is a few hundred bytes; one declared far larger is refused at once.
        using var large = await client.PostAsync(url, new StringContent(new string('x', 20_000))).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);

        // A normal one still reaches Jellyfin, and the slots are free again afterwards.
        for (var i = 0; i < 3; i++)
        {
            using var normal = await client.PostAsync(url, new StringContent("{}")).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.Unauthorized, normal.StatusCode);
        }
    }

    [Fact]
    public async Task ConnectionFilter_RefusesBeforeTls_LocalNeverFiltered()
    {
        var refuseAll = new RefuseAll();
        await using (var proxy = await StartProxyAsync(filter: refuseAll).ConfigureAwait(false))
        {
            using var client = Client(proxy.HttpsPort);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"https://{Host}:{proxy.HttpsPort}/System/Info/Public"))).ConfigureAwait(false);
            Assert.True(refuseAll.Calls > 0);
        }

        // The same filter never sees a local client.
        var local = new RefuseAll();
        await using var lan = await StartProxyAsync(loopbackIsLocal: true, filter: local).ConfigureAwait(false);
        using var lanClient = Client(lan.HttpPort);
        await lanClient.GetStringAsync(new Uri($"http://127.0.0.1:{lan.HttpPort}/System/Info/Public")).ConfigureAwait(false);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task Jellyfin_ARequestThatAlreadyPassedThisProxy_IsRefused()
    {
        // A Service pointed back at this proxy would bring an internet visitor to Jellyfin from this machine.
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        using var looped = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}:{proxy.HttpsPort}/System/Info/Public");
        looped.Headers.TryAddWithoutValidation(ProxyPolicyMiddleware.HopHeader, ProxyPolicyMiddleware.NextHop(null));
        using var refused = await client.SendAsync(looped).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.LoopDetected, refused.StatusCode);

        // Another server's count is not this one's.
        using var other = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}:{proxy.HttpsPort}/System/Info/Public");
        other.Headers.TryAddWithoutValidation(ProxyPolicyMiddleware.HopHeader, "elsewhere:1");
        using var allowed = await client.SendAsync(other).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task NoCaching_JellyfinAnswersEveryRequest()
    {
        // Jellyfin keeps resized images on disk and tells clients to cache them, so the proxy keeps no copy.
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        var uri = new Uri($"https://{Host}:{proxy.HttpsPort}/web/main.js");
        await client.GetStringAsync(uri).ConfigureAwait(false);
        await client.GetStringAsync(uri).ConfigureAwait(false);
        Assert.Equal(2, _webAssetHits);
    }

    [Fact]
    public async Task Compression_AppliesToJellyfinText()
    {
        await using var proxy = await StartProxyAsync().ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort, decompress: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}:{proxy.HttpsPort}/web/main.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task RouteOverrides_UploadLimitAndHsts()
    {
        await using var proxy = await StartProxyAsync(edit: s =>
            s.Routes.Add(new ProxyRoute
            {
                Id = "small",
                Hostnames = new List<string> { RouteHost },
                Target = _serviceUri.ToString(),
                Overrides = new RouteOverrides { MaxRequestBodyMbWan = 1, HstsEnabled = false }
            })).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);
        var upload = new Uri($"https://{RouteHost}:{proxy.HttpsPort}/upload");

        using var small = await client.PostAsync(upload, new ByteArrayContent(new byte[512 * 1024])).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        Assert.False(small.Headers.Contains("Strict-Transport-Security"));

        // Refused either as a 413, or by the connection closing while the client is still sending; which one
        // the client sees is a race. Either way the upload must never reach the service.
        var uploadsBefore = _uploads;
        try
        {
            using var large = await client.PostAsync(upload, new ByteArrayContent(new byte[2 * 1024 * 1024])).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        }
        catch (HttpRequestException)
        {
        }

        Assert.Equal(uploadsBefore, _uploads);
    }

    [Fact]
    public async Task Logs_NeverContainTokens()
    {
        await using var proxy = await StartProxyAsync(edit: s =>
            s.Routes.Add(new ProxyRoute { Id = "dead", Hostnames = new List<string> { "dead.example.com" }, Target = "http://127.0.0.1:" + FreePort() })).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort);

        // A route whose backend is down makes YARP log a forwarding error for a URL carrying a token.
        using var response = await client.GetAsync(new Uri($"https://dead.example.com:{proxy.HttpsPort}/Videos/1/stream?api_key=SUPERSECRET")).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(_logs.Messages, m => m.Contains("SUPERSECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionGate_CapsConnectionsPerAddress()
    {
        using var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime(Settings(s => s.Advanced.MaxConnectionsPerIp = 2), loopbackIsLocal: false)), Array.Empty<IConnectionFilter>());
        var hold = new TaskCompletionSource();
        Task Open() => gate.OnConnectionAsync(
            new Microsoft.AspNetCore.Connections.DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 1) },
            _ => hold.Task);

        var first = Open();
        var second = Open();
        Assert.True(Open().IsCompleted);
        Assert.False(first.IsCompleted || second.IsCompleted);

        hold.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert.Equal(0, gate.TrackedAddresses);
        Assert.True(Open().IsCompleted);
    }

    private static Dictionary<string, string> Echo(HttpContext ctx, string backend) => new()
    {
        ["backend"] = backend,
        ["path"] = ctx.Request.Path.Value ?? string.Empty,
        ["host"] = ctx.Request.Host.Value ?? string.Empty,
        ["x-forwarded-for"] = ctx.Request.Headers["X-Forwarded-For"].ToString(),
        ["x-forwarded-proto"] = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
        ["x-real-ip"] = ctx.Request.Headers["X-Real-IP"].ToString(),
        ["forwarded"] = ctx.Request.Headers["Forwarded"].ToString(),
        ["route-header"] = ctx.Request.Headers[ProxyPolicyMiddleware.RouteHeader].ToString()
    };

    private static async Task<Dictionary<string, string>> ReadEcho(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(body)!;
    }

    private static async Task<(WebApplication App, Uri Uri)> StartBackendAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        map(app);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ProxySettings Settings(Action<ProxySettings>? edit = null)
    {
        var s = new ProxySettings { Enabled = true, Hostnames = new List<string> { Host } };
        edit?.Invoke(s);
        ProxySettingsValidator.Normalize(s);
        return s;
    }

    private ProxyRuntime Runtime(ProxySettings settings, bool loopbackIsLocal)
    {
        var view = new JellyfinNetworkView(_jellyfinUri ?? new Uri("http://127.0.0.1:1/"), string.Empty, true, 8097, 0);
        var runtime = ProxyRuntimeFactory.Build(settings, view, _ => false, httpsAvailable: true);
        var listener = runtime.Listener with { HttpPort = 0, HttpsPort = 0, LoopbackOnly = true };
        return new ProxyRuntime
        {
            Listener = listener,
            JellyfinHostnames = runtime.JellyfinHostnames,
            Routes = runtime.Routes,
            JellyfinBackend = runtime.JellyfinBackend,
            BaseUrl = runtime.BaseUrl,
            JellyfinTrustsForwarding = runtime.JellyfinTrustsForwarding,
            Options = runtime.Options,
            Classifier = new NetworkClassifier(_ => false, Array.Empty<IPNetwork>(), loopbackIsLocal)
        };
    }

    private async Task<RunningProxy> StartProxyAsync(Action<ProxySettings>? edit = null, bool loopbackIsLocal = false, IAccessRecorder? recorder = null, IConnectionFilter? filter = null)
    {
        var settings = Settings(edit);
        settings.Certificate.Mode = CertificateMode.SelfSigned;
        var runtime = Runtime(settings, loopbackIsLocal);
        var certificates = new CertificateStore(new ProxyPaths(_dir), new SecretProtector("test", NullLogger.Instance), NullLogger<CertificateStore>.Instance);
        certificates.Configure(settings.Certificate, runtime.AllHostnames());
        var host = await ProxyHost.StartAsync(
            new ProxyHostOptions
            {
                Holder = new ProxyRuntimeHolder(runtime),
                Certificates = certificates,
                Challenges = new Http01ChallengeStore(),
                LoggerFactory = _logs,
                AccessRecorder = recorder,
                ConnectionFilters = filter is null ? Array.Empty<IConnectionFilter>() : new[] { filter },
                ContentRoot = _dir
            },
            CancellationToken.None).ConfigureAwait(false);
        return new RunningProxy(host, certificates);
    }

    private static HttpClient Client(int port, bool decompress = true, bool followRedirects = true)
    {
        // Every name resolves to loopback, so requests carry real hostnames for SNI and Host.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed class SwitchFilter : IConnectionFilter
    {
        public bool Refuse { get; set; }

        public bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true) => !Refuse;
    }

    private sealed class RefuseAll : IConnectionFilter
    {
        private int _calls;

        public int Calls => _calls;

        public bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true)
        {
            Interlocked.Increment(ref _calls);
            return false;
        }
    }

    private sealed class RunningProxy : IAsyncDisposable
    {
        private readonly CertificateStore _certificates;

        public RunningProxy(ProxyHost host, CertificateStore certificates)
        {
            Host = host;
            _certificates = certificates;
        }

        public ProxyHost Host { get; }

        public int HttpPort => Port(Uri.UriSchemeHttp);

        public int HttpsPort => Port(Uri.UriSchemeHttps);

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Host.DisposeAsync().ConfigureAwait(false);
            _certificates.Dispose();
        }

        private int Port(string scheme) => new Uri(Host.Addresses.First(a => a.StartsWith(scheme + "://", StringComparison.Ordinal))).Port;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capture : ILogger
        {
            private readonly CapturingLoggerFactory _owner;

            public Capture(CapturingLoggerFactory owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_owner._messages)
                {
                    _owner._messages.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
                }
            }
        }
    }
}
