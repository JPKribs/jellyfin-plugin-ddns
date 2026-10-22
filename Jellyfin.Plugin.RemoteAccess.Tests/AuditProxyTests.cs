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
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Fixes from the proxy audit: request and stream limits, fair connection sharing, partial masking, wide ban
/// escalation, rechecks on locality changes and path folding.
/// </summary>
public sealed class AuditProxyTests : IAsyncLifetime
{
    private const string Host = "media.example.com";

    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-audit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _jellyfin;
    private Uri _jellyfinUri = null!;
    private int _slowHits;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        (_jellyfin, _jellyfinUri) = await StartBackendAsync(app =>
        {
            app.MapGet("/slow", async () =>
            {
                Interlocked.Increment(ref _slowHits);
                await _release.Task.ConfigureAwait(false);
                return Results.Text("done");
            });
            app.Map("/{**rest}", () => Results.Text("jellyfin"));
        }).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        if (_jellyfin is not null)
        {
            await _jellyfin.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // A file still held open is left for the system's temporary-file cleanup.
        }
    }

    [Fact]
    public void RequestLimit_IsFourTimesEachConnectionCap_AndReleases()
    {
        using var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime()), Array.Empty<IConnectionFilter>());
        var options = ProxyOptions.Standard with { MaxConnectionsPerIp = 2, MaxConnections = 2000 };
        var address = IPAddress.Parse("203.0.113.5");
        var held = Enumerable.Range(0, 8).Select(_ => gate.TryEnterRequest(address, null, options)).ToList();
        Assert.All(held, Assert.NotNull);
        Assert.Null(gate.TryEnterRequest(address, null, options));

        // Another address is unaffected, and a finished request frees its slot.
        Assert.NotNull(gate.TryEnterRequest(IPAddress.Parse("203.0.113.6"), null, options));
        gate.ExitRequest(held[0]!);
        Assert.NotNull(gate.TryEnterRequest(address, null, options));

        // Many /64s in one /56 share four times the /56's connection cap.
        var wide = Enumerable.Range(0, 40)
            .Count(i => gate.TryEnterRequest(IPAddress.Parse($"2001:db8:9:{i:x}::1"), null, options) is not null);
        Assert.Equal(4 * ConnectionGate.WideLimit(2, 2000), wide);
    }

    [Fact]
    public async Task RequestLimit_RefusesExtraHttp2Streams_From_OneAddress()
    {
        // One connection per address allowed, so four requests in flight; HTTP/2 would otherwise carry 32.
        await using var proxy = await StartProxyAsync(s => s.Advanced.MaxConnectionsPerIp = 1).ConfigureAwait(false);
        using var client = Client(proxy.HttpsPort, http2: true);
        var url = new Uri($"https://{Host}:{proxy.HttpsPort}/slow");
        var requests = Enumerable.Range(0, 6).Select(_ => client.GetAsync(url)).ToList();

        var refused = new List<HttpResponseMessage>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (refused.Count < 2 && DateTime.UtcNow < deadline)
        {
            var done = await Task.WhenAny(requests).ConfigureAwait(false);
            requests.Remove(done);
            refused.Add(await done.ConfigureAwait(false));
        }

        Assert.All(refused, r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.All(refused, r => Assert.Equal(new Version(2, 0), r.Version));

        // The four let through reach Jellyfin and wait there.
        while (Volatile.Read(ref _slowHits) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Equal(4, Volatile.Read(ref _slowHits));
        Assert.All(requests, r => Assert.False(r.IsCompleted));

        _release.SetResult();
        foreach (var response in await Task.WhenAll(requests).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        foreach (var response in refused)
        {
            response.Dispose();
        }
    }

    [Fact]
    public void Shares_Slash56AndSlash48_AreSmallPartsOfTheWhole_NeverBelowOneAddress()
    {
        Assert.Equal(125, ConnectionGate.WideLimit(64, 8000));
        Assert.Equal(250, ConnectionGate.NetworkLimit(64, 8000));

        // The defaults: a share below one address's worth is raised to it.
        Assert.Equal(64, ConnectionGate.WideLimit(64, 2000));
        Assert.Equal(64, ConnectionGate.NetworkLimit(64, 2000));

        // A few addresses' worth when the share is larger.
        Assert.Equal(4, ConnectionGate.WideLimit(1, 2000));
        Assert.Equal(8, ConnectionGate.NetworkLimit(1, 2000));
        Assert.Equal(256, ConnectionGate.WideLimit(64, 0));
    }

    [Fact]
    public void FullLimit_ClosesTheOldestOfTheBiggestBlock_ForANewcomer()
    {
        var counter = new LimitCounter();
        var holder = new ProxyRuntimeHolder(Runtime(s => { s.Advanced.MaxConnections = 4; s.Advanced.MaxConnectionsPerIp = 4; }));
        using var gate = new ConnectionGate(holder, new[] { counter });
        var never = new TaskCompletionSource();
        var hog = Enumerable.Range(0, 4).Select(_ => Context("203.0.113.1")).ToList();
        foreach (var context in hog)
        {
            Assert.False(gate.OnConnectionAsync(context, _ => never.Task).IsCompleted);
        }

        // The whole limit is full of one address's idle connections. A newcomer gets in, the oldest goes.
        var newcomer = Context("198.51.100.7");
        Assert.False(gate.OnConnectionAsync(newcomer, _ => never.Task).IsCompleted);
        Assert.True(hog[0].ConnectionClosed.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.False(hog[1].ConnectionClosed.IsCancellationRequested);
        Assert.Equal(0, counter.Count);

        // The hog itself gets no room by closing its own, and the refusal is counted.
        Assert.True(gate.OnConnectionAsync(Context("203.0.113.1"), _ => never.Task).IsCompleted);
        Assert.Equal(1, counter.Count);
        never.SetResult();
    }

    [Fact]
    public void FullLimit_OfEquals_RefusesAndCounts()
    {
        var counter = new LimitCounter();
        using var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime(s => s.Advanced.MaxConnections = 2)), new[] { counter });
        var never = new TaskCompletionSource();
        var first = Context("203.0.113.1");
        _ = gate.OnConnectionAsync(first, _ => never.Task);
        _ = gate.OnConnectionAsync(Context("203.0.113.2"), _ => never.Task);

        // Each holds one, so nobody is closed to make room.
        Assert.True(gate.OnConnectionAsync(Context("203.0.113.3"), _ => never.Task).IsCompleted);
        Assert.False(first.ConnectionClosed.IsCancellationRequested);
        Assert.Equal(1, counter.Count);
        never.SetResult();
    }

    [Fact]
    public void RefusalTotals_ShowTheConnectionLimits()
    {
        var totals = new RefusalTotals(1, 0, 0) { Limit = 2 } + new RefusalTotals(0, 0, 0) { Limit = 1 };
        Assert.Equal(4, totals.Total);
        Assert.Equal("1 by the block list, 3 by the connection limits", totals.Describe());
    }

    [Fact]
    public void Recheck_ClosesAConnectionWhoseTrustedNetworkWasRemoved()
    {
        var holder = new ProxyRuntimeHolder(Runtime(s => s.Advanced.TrustedNetworks = new List<string> { "198.51.100.0/24" }));
        using var gate = new ConnectionGate(holder, Array.Empty<IConnectionFilter>());
        var never = new TaskCompletionSource();
        var trusted = Context("198.51.100.20");
        var lan = Context("192.168.1.30");
        _ = gate.OnConnectionAsync(trusted, _ => never.Task);
        _ = gate.OnConnectionAsync(lan, _ => never.Task);
        Assert.Equal(0, gate.InternetConnections);

        holder.Current = Runtime();
        Assert.True(trusted.ConnectionClosed.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.False(lan.ConnectionClosed.IsCancellationRequested);
        never.SetResult();
    }

    [Fact]
    public void LocalTraffic_OneLocalAddressCarryingMost_IsFlagged_EvenWithInternetVisitors()
    {
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var dockerProxy = IPAddress.Parse("172.17.0.1");
        var watch = new LocalTrafficWatch();
        for (var i = 0; i < 80; i++)
        {
            watch.Observe(false, true, IPAddress.Parse("203.0.113." + (i % 50)), start.AddSeconds(i));
        }

        for (var i = 0; i < 150; i++)
        {
            watch.Observe(true, true, dockerProxy, start.AddMinutes(2).AddSeconds(i));
        }

        Assert.True(watch.Suspected);
        Assert.True(watch.Partial);

        // Busy LAN clients alongside a busier internet are normal.
        var normal = new LocalTrafficWatch();
        for (var i = 0; i < 300; i++)
        {
            normal.Observe(false, true, IPAddress.Parse("203.0.113.9"), start.AddSeconds(i));
        }

        for (var i = 0; i < 150; i++)
        {
            normal.Observe(true, true, IPAddress.Parse("192.168.1.40"), start.AddMinutes(10).AddSeconds(i));
        }

        Assert.False(normal.Suspected);
    }

    [Fact]
    public void LocalTraffic_Loopback_CountsOnlyForPublicNames()
    {
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var quiet = new LocalTrafficWatch();
        for (var i = 0; i < 300; i++)
        {
            quiet.Observe(true, false, IPAddress.Loopback, start.AddSeconds(i));
        }

        Assert.False(quiet.Suspected);

        // A tunnel on this machine hands visitors over from loopback, asking for the public names.
        var tunnel = new LocalTrafficWatch();
        for (var i = 0; i < 150; i++)
        {
            tunnel.Observe(true, true, IPAddress.IPv6Loopback, start.AddSeconds(i));
        }

        Assert.True(tunnel.Suspected);
    }

    [Fact]
    public void WideBans_DoubleForRepeatOffenders_UpToTheCap()
    {
        var bans = Bans();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        const string Key = "2001:db8::/56";
        Assert.Equal(15, bans.WideMinutes(Key, now, 60));
        Assert.Equal(30, bans.WideMinutes(Key, now.AddMinutes(20), 60));
        Assert.Equal(60, bans.WideMinutes(Key, now.AddMinutes(60), 60));
        Assert.Equal(60, bans.WideMinutes(Key, now.AddMinutes(130), 60));

        // A day without a ban starts over.
        Assert.Equal(15, bans.WideMinutes(Key, now.AddDays(2), 60));

        // Bans that last until lifted double up to a day.
        const string Other = "2001:db8:1::/48";
        var minutes = Enumerable.Range(0, 10).Select(i => bans.WideMinutes(Other, now.AddHours(i), 0)).ToList();
        Assert.Equal(new[] { 15, 30, 60, 120, 240, 480, 960, 1440, 1440, 1440 }, minutes);

        // A short configured ban stays the cap.
        Assert.Equal(5, bans.WideMinutes("2001:db8:2::/48", now, 5));
    }

    [Fact]
    public void WideBans_RepeatedThroughFailures_LastLonger()
    {
        var bans = Bans();
        var options = ProxyOptions.Standard with { BanFailures = 5, BanWindowMinutes = 10, BanMinutes = 60 };
        Ban SlashFiftySix()
        {
            for (var net = 0; net < 4; net++)
            {
                for (var i = 0; i < 4; i++)
                {
                    if (bans.RecordFailure(IPAddress.Parse($"2001:db8:0:2{net}::{i + 1}"), options))
                    {
                        return bans.Active().Single();
                    }
                }
            }

            throw new InvalidOperationException("No ban");
        }

        var first = SlashFiftySix();
        Assert.Equal(TimeSpan.FromMinutes(15), first.UntilUtc - first.BannedUtc);

        // The next ban of the same /56 within a day is twice as long.
        Assert.Equal(30, bans.WideMinutes(first.Ip, DateTime.UtcNow, 60));

        // Lifting the ban by hand starts it over.
        bans.Unban(first.Ip);
        Assert.Equal(15, bans.WideMinutes(first.Ip, DateTime.UtcNow, 60));
    }

    [Fact]
    public void SavedBans_AreReadWhenTheGateIsBuilt()
    {
        var store = new PeopleStore(new ProxyPaths(_dir), NullLogger<PeopleStore>.Instance);
        store.SaveBan(new Ban { Ip = "203.0.113.77", BannedUtc = DateTime.UtcNow, UntilUtc = DateTime.UtcNow.AddHours(1) });
        var geo = new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance);
        var bans = new BanList(store, geo, NullLogger<BanList>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_dir);
        var server = new ServerLocation(
            new IPDetectionService(http, NullLogger<IPDetectionService>.Instance),
            new StatusStoreService(paths, NullLogger<StatusStoreService>.Instance),
            geo,
            NullLogger<ServerLocation>.Instance);
        var filter = new BlockFilter(geo, server, bans, NullLogger<BlockFilter>.Instance);

        // Deleting the row after the gate is built shows the bans were read then, not on first use.
        using var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime()), new IConnectionFilter[] { filter });
        store.DeleteBan("203.0.113.77");
        Assert.True(bans.IsBanned(IPAddress.Parse("203.0.113.77")));
    }

    [Theory]
    [InlineData("//Startup/User", "", true)]
    [InlineData("/Startup//User", "", true)]
    [InlineData("/jellyfin//Startup/User", "/jellyfin", true)]
    [InlineData("//jellyfin/Startup", "/jellyfin", true)]
    [InlineData("/StartupX", "", false)]
    public void Paths_RepeatedSlashes_AreFoldedForStartup(string path, string baseUrl, bool startup)
        => Assert.Equal(startup, PathRules.IsStartup(PathRules.StripBase(new PathString(path), baseUrl)));

    [Theory]
    [InlineData("/jellyfin//Users/AuthenticateByName", "/jellyfin")]
    [InlineData("//Users//AuthenticateByName", "")]
    [InlineData("/Users//AuthenticateWithQuickConnect/", "")]
    [InlineData("//Users/abc//Authenticate", "")]
    public void Paths_RepeatedSlashes_AreFoldedForSignIn(string path, string baseUrl)
        => Assert.True(PathRules.IsSignIn(PathRules.StripBase(new PathString(path), baseUrl)));

    [Fact]
    public void Paths_WithoutDoubledSlashes_AreReturnedAsIs()
    {
        var path = new PathString("/Users/AuthenticateByName");
        Assert.Same(path.Value, PathRules.Collapse(path).Value);
        Assert.Equal("/a/b/", PathRules.Collapse(new PathString("//a///b//")).Value);
    }

    [Fact]
    public void Unclaimed_PairsEachReportedFailureOnce()
    {
        var recorder = new AccessRecorder();
        var ip = IPAddress.Parse("203.0.113.40");
        recorder.RecordSignInFailure(ip, "https://media.example.com");
        recorder.RecordSignInFailure(ip, "https://media.example.com");
        recorder.RecordSignInFailure(IPAddress.Parse("203.0.113.41"), "https://media.example.com");
        recorder.Claim("203.0.113.40");

        var unclaimed = recorder.TakeUnclaimed(TimeSpan.Zero);
        Assert.Equal(new[] { "203.0.113.40", "203.0.113.41" }, unclaimed.Select(f => f.Ip).OrderBy(i => i, StringComparer.Ordinal));
    }

    private static DefaultConnectionContext Context(string ip)
        => new() { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), 1234) };

    private BanList Bans()
    {
        var store = new PeopleStore(new ProxyPaths(_dir), NullLogger<PeopleStore>.Instance);
        var geo = new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance);
        return new BanList(store, geo, NullLogger<BanList>.Instance);
    }

    private static ProxyRuntime Runtime(Action<ProxySettings>? edit = null)
    {
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { Host } };
        edit?.Invoke(settings);
        ProxySettingsValidator.Normalize(settings);
        var view = new JellyfinNetworkView(new Uri("http://127.0.0.1:8096/"), string.Empty, true, 8096, 0);
        return ProxyRuntimeFactory.Build(settings, view, ip => ip.ToString().StartsWith("192.168.", StringComparison.Ordinal), httpsAvailable: true);
    }

    private ProxyRuntime HostRuntime(ProxySettings settings)
    {
        var view = new JellyfinNetworkView(_jellyfinUri, string.Empty, true, 8097, 0);
        var runtime = ProxyRuntimeFactory.Build(settings, view, _ => false, httpsAvailable: true);
        return new ProxyRuntime
        {
            Listener = runtime.Listener with { HttpPort = 0, HttpsPort = 0, LoopbackOnly = true },
            JellyfinHostnames = runtime.JellyfinHostnames,
            JellyfinBackend = runtime.JellyfinBackend,
            BaseUrl = runtime.BaseUrl,
            JellyfinTrustsForwarding = runtime.JellyfinTrustsForwarding,
            Options = runtime.Options,
            Classifier = new NetworkClassifier(_ => false, Array.Empty<IPNetwork>(), loopbackIsLocal: false)
        };
    }

    private async Task<RunningProxy> StartProxyAsync(Action<ProxySettings>? edit = null)
    {
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { Host } };
        edit?.Invoke(settings);
        ProxySettingsValidator.Normalize(settings);
        settings.Certificate.Mode = CertificateMode.SelfSigned;
        var runtime = HostRuntime(settings);
        var certificates = new CertificateStore(new ProxyPaths(_dir), new SecretProtector("test", NullLogger.Instance), NullLogger<CertificateStore>.Instance);
        certificates.Configure(settings.Certificate, runtime.JellyfinHostnames);
        var host = await ProxyHost.StartAsync(
            new ProxyHostOptions
            {
                Holder = new ProxyRuntimeHolder(runtime),
                Certificates = certificates,
                Challenges = new Http01ChallengeStore(),
                LoggerFactory = NullLoggerFactory.Instance,
                ContentRoot = _dir
            },
            CancellationToken.None).ConfigureAwait(false);
        return new RunningProxy(host, certificates);
    }

    private static HttpClient Client(int port, bool http2 = false)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (http2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
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

    private sealed class LimitCounter : IConnectionFilter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true) => true;

        public void RecordLimited(IPAddress address) => Interlocked.Increment(ref _count);
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

        public int HttpsPort => new Uri(Host.Addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal))).Port;

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Host.DisposeAsync().ConfigureAwait(false);
            _certificates.Dispose();
        }
    }
}
