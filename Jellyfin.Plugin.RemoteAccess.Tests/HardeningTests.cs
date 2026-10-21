using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Acme;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Jellyfin.Plugin.DynamicDns.Utilities;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.DataProtection;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Protections added after the adversarial audit: wider bans, the connection gate, Service targets, kept
/// secrets, certificate timing, Undo and the visit history.
/// </summary>
public sealed class HardeningTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly BanList _bans;

    public HardeningTests()
    {
        Directory.CreateDirectory(_dir);
        var store = new PeopleStore(new ProxyPaths(_dir), NullLogger<PeopleStore>.Instance);
        var geo = new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance);
        _bans = new BanList(store, geo, NullLogger<BanList>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // A file still held open is left for the system's temporary-file cleanup.
        }
    }

    private static ProxyOptions Options(int failures = 5) => ProxyOptions.Standard with { BanFailures = failures, BanWindowMinutes = 10, BanMinutes = 60 };

    private static ProxyRuntime Runtime(Action<ProxySettings>? edit = null, int jellyfinPort = 8096)
    {
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { "media.example.com" } };
        edit?.Invoke(settings);
        ProxySettingsValidator.Normalize(settings);
        var view = new JellyfinNetworkView(new Uri("http://127.0.0.1:" + jellyfinPort + "/"), string.Empty, true, jellyfinPort, 0);
        var built = ProxyRuntimeFactory.Build(settings, view, ip => ip.ToString().StartsWith("192.168.", StringComparison.Ordinal), httpsAvailable: true);
        return built;
    }

    [Fact]
    public void Bans_SpreadOverManySlash64s_BanTheSlash56()
    {
        // Four guesses from each of four /64s in one /56: no single /64 reaches five.
        var banned = false;
        for (var net = 0; net < 4 && !banned; net++)
        {
            for (var i = 0; i < 4 && !banned; i++)
            {
                banned = _bans.RecordFailure(IPAddress.Parse($"2001:db8:0:1{net}::{i + 1}"), Options());
            }
        }

        Assert.True(banned);

        // A wider ban shuts out everyone in it, so it lasts at most 15 minutes, not the 60 a single address gets.
        var wide = Assert.Single(_bans.Active(), b => b.Ip == "2001:db8::/56");
        Assert.InRange((wide.UntilUtc!.Value - wide.BannedUtc).TotalMinutes, 14.9, 15.1);

        // A fresh /64 in the same /56 is refused too.
        Assert.True(_bans.IsBanned(IPAddress.Parse("2001:db8:0:ff::1")));
        Assert.Contains(_bans.Active(), b => b.Ip == "2001:db8::/56");
        Assert.False(_bans.IsBanned(IPAddress.Parse("2001:db8:0:100::1")));
    }

    [Fact]
    public void Bans_AnAttackerMovingToAFreshSlash64AfterEachBan_IsCaughtByTheSlash56()
    {
        // Five guesses per /64, the most each allows, then a fresh /64 in the same /56.
        string? wide = null;
        for (var net = 0; net < 10 && wide is null; net++)
        {
            for (var i = 0; i < 5; i++)
            {
                _bans.RecordFailure(IPAddress.Parse($"2001:db8:0:{net:x}::{i + 1}"), Options());
            }

            wide = _bans.Active().Select(b => b.Ip).FirstOrDefault(k => k.EndsWith("/56", StringComparison.Ordinal));
        }

        Assert.Equal("2001:db8::/56", wide);
        Assert.True(_bans.IsBanned(IPAddress.Parse("2001:db8:0:ff::1")));
    }

    [Fact]
    public void Bans_AFullTracker_ForgetsTheOldestCountsButKeepsRecentOnes()
    {
        _bans.RecordFailure(IPAddress.Parse("198.51.100.1"), Options(2));
        for (var i = 0; i < 60_000; i++)
        {
            _bans.RecordFailure(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), Options(2));
        }

        // The first address's count went with the oldest tenth, the newest one's stayed.
        Assert.False(_bans.RecordFailure(IPAddress.Parse("198.51.100.1"), Options(2)));
        Assert.True(_bans.RecordFailure(new IPAddress(new byte[] { 10, 0, 234, 95 }), Options(2)));
    }

    [Fact]
    public void Bans_LiftingAVisitorsBan_LiftsTheWiderOnesHoldingItToo()
    {
        for (var net = 0; net < 10 && _bans.BanFor(IPAddress.Parse("2001:db8:0:ff::1")) is null; net++)
        {
            for (var i = 0; i < 5; i++)
            {
                _bans.RecordFailure(IPAddress.Parse($"2001:db8:0:{net:x}::{i + 1}"), Options());
            }
        }

        var visitor = IPAddress.Parse("2001:db8:0:9::1");
        Assert.NotNull(_bans.BanFor(visitor));
        var lifted = _bans.UnbanAll(visitor);
        Assert.Contains("2001:db8::/56", lifted);
        Assert.Null(_bans.BanFor(visitor));
    }

    [Fact]
    public void KeyRing_WrappedKeys_StillDecryptSavedSecrets()
    {
        var folder = Path.Join(_dir, "ring");
        Directory.CreateDirectory(folder);
        var provider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(folder), b => b.SetApplicationName("ring-test"));
        var secret = provider.CreateProtector("p").Protect("hunter2");

        // Wrapped with the no-op encryptor, which writes the same form DPAPI's does.
        var wrapped = Directory.GetFiles(folder, "key-*.xml").Count(f => KeyRingProtection.WrapFile(
            f,
            new Microsoft.AspNetCore.DataProtection.XmlEncryption.NullXmlEncryptor(),
            new Microsoft.AspNetCore.DataProtection.XmlEncryption.NullXmlDecryptor()));
        Assert.True(wrapped > 0);
        Assert.Contains("encryptedSecret", File.ReadAllText(Directory.GetFiles(folder, "key-*.xml")[0]), StringComparison.Ordinal);

        // A fresh key ring reading the folder, as after a restart, still decrypts what was saved.
        var reread = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(folder), b => b.SetApplicationName("ring-test"));
        Assert.Equal("hunter2", reread.CreateProtector("p").Unprotect(secret));
    }

    [Fact]
    public void Bans_AlreadyBannedAddress_IsNotBannedAgain()
    {
        var ip = IPAddress.Parse("203.0.113.50");
        for (var i = 0; i < 5; i++)
        {
            _bans.RecordFailure(ip, Options());
        }

        var first = Assert.Single(_bans.Active());

        // Answers to guesses already in flight arrive after the ban: no new ban, and no second round of closing.
        Assert.False(_bans.RecordFailure(ip, Options()));
        Assert.Same(first, Assert.Single(_bans.Active()));
    }

    [Fact]
    public void Gate_SignInSlots_TwoPerGroup()
    {
        var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime()), Array.Empty<IConnectionFilter>());
        var a = IPAddress.Parse("2001:db8:5:6::1");
        var sameGroup = IPAddress.Parse("2001:db8:5:6::2");
        Assert.True(gate.TryEnterSignIn(a));
        Assert.True(gate.TryEnterSignIn(sameGroup));
        Assert.False(gate.TryEnterSignIn(a));
        Assert.True(gate.TryEnterSignIn(IPAddress.Parse("2001:db8:5:7::1")));
        gate.ExitSignIn(a);
        Assert.True(gate.TryEnterSignIn(a));
    }

    [Fact]
    public async Task Gate_LocalClientsDoNotUseTheLimit_AndASlash56IsCapped()
    {
        var holder = new ProxyRuntimeHolder(Runtime(s => s.Advanced.MaxConnections = 2));
        using var gate = new ConnectionGate(holder, Array.Empty<IConnectionFilter>());
        var hold = new TaskCompletionSource();
        var open = new List<(DefaultConnectionContext Context, Task Task)>();
        Task Open(string ip)
        {
            var context = new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), 1234) };
            var task = gate.OnConnectionAsync(context, _ => hold.Task);
            open.Add((context, task));
            return task;
        }

        _ = Open("203.0.113.1");
        _ = Open("203.0.113.2");
        Assert.True(Open("203.0.113.3").IsCompleted);

        // The internet has used the limit, but the local network still gets in.
        Assert.False(Open("192.168.1.20").IsCompleted);
        Assert.Equal(2, gate.InternetConnections);
        hold.SetResult();
        await Task.WhenAll(open.Select(o => o.Task));
        Assert.Equal(0, gate.InternetConnections);

        // Many /64s inside one /56 share four addresses' worth.
        var wide = new ConnectionGate(new ProxyRuntimeHolder(Runtime(s => s.Advanced.MaxConnectionsPerIp = 1)), Array.Empty<IConnectionFilter>());
        var never = new TaskCompletionSource();
        var accepted = Enumerable.Range(0, 8)
            .Select(i => wide.OnConnectionAsync(new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse($"2001:db8:0:1{i}::1"), 1) }, _ => never.Task))
            .Count(t => !t.IsCompleted);
        Assert.Equal(4, accepted);
        never.SetResult();
    }

    [Fact]
    public void Gate_NewRule_ClosesOpenConnections()
    {
        var filter = new Switch();
        var holder = new ProxyRuntimeHolder(Runtime());
        using var gate = new ConnectionGate(holder, new[] { filter });
        var context = new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 1) };
        var never = new TaskCompletionSource();
        _ = gate.OnConnectionAsync(context, _ => never.Task);
        Assert.False(context.ConnectionClosed.IsCancellationRequested);

        // Aborting signals the connection on the thread pool, so the close is awaited briefly.
        filter.Refuse = true;
        holder.Current = Runtime();
        Assert.True(context.ConnectionClosed.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        never.SetResult();
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096", true)]
    [InlineData("http://localhost:8096/web", true)]
    [InlineData("https://[::1]:443", true)]
    [InlineData("http://127.0.0.1:5055", false)]
    [InlineData("http://192.0.2.10:8096", false)]
    public void SelfTarget_JellyfinAndProxyPortsOnThisMachine(string target, bool reserved)
        => Assert.Equal(reserved, SelfTarget.IsReserved(new Uri(target), new[] { 8096, 0, 80, 443 }));

    [Fact]
    public void SelfTarget_RefusedAtSave_AndNeverServed()
    {
        void Route(ProxySettings s) => s.Routes.Add(new ProxyRoute { Name = "Loop", Hostnames = new List<string> { "loop.example.com" }, Target = "http://127.0.0.1:8096" });
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { "media.example.com" } };
        Route(settings);
        ProxySettingsValidator.Normalize(settings);
        Assert.Contains(ProxySettingsValidator.Validate(settings, null, new[] { 8096, 80, 443 }), e => e.Contains("points at Jellyfin", StringComparison.Ordinal));
        Assert.Empty(Runtime(Route).Routes);
    }

    [Fact]
    public void Compression_ServicesOffUnlessAsked_JellyfinNever()
    {
        var runtime = Runtime(s =>
        {
            s.Routes.Add(new ProxyRoute { Name = "App", Hostnames = new List<string> { "app.example.com" }, Target = "http://192.168.1.20:5055" });
            s.Routes.Add(new ProxyRoute { Name = "Packed", Hostnames = new List<string> { "packed.example.com" }, Target = "http://192.168.1.21:5055", Overrides = new RouteOverrides { Compress = true } });
        });
        var app = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        app.Items["ddns.proxy.route"] = runtime.Routes[0];
        Assert.False(ProxyPolicyMiddleware.ShouldCompress(app, runtime));

        var packed = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        packed.Items["ddns.proxy.route"] = runtime.Routes[1];
        Assert.True(ProxyPolicyMiddleware.ShouldCompress(packed, runtime));

        // Jellyfin compresses its own replies.
        Assert.False(ProxyPolicyMiddleware.ShouldCompress(new Microsoft.AspNetCore.Http.DefaultHttpContext(), runtime));
    }

    [Fact]
    public void KeptSecrets_DoNotFollowARecordToANewServer()
    {
        var existing = new List<DNSRecord> { new() { Id = "a", Provider = DNSProviderKind.DynDns1, Server = "https://members.example.com", Login = "enc:v1:login", Password = "enc:v1:pass" } };
        var moved = new List<DNSRecord> { new() { Id = "a", Provider = DNSProviderKind.DynDns1, Server = "https://attacker.test", Login = "", Password = "" } };
        var merged = RecordMerge.Apply(existing, moved, (submitted, stored) => string.IsNullOrEmpty(submitted) ? stored ?? string.Empty : submitted, () => "x");
        Assert.Equal(string.Empty, merged[0].Login);
        Assert.Equal(string.Empty, merged[0].Password);
    }

    [Fact]
    public void Ciphertext_OnlyKeptWhereItCameFrom()
    {
        var stored = new List<DNSRecord> { new() { Id = "a", Provider = DNSProviderKind.Cloudflare, Password = "enc:v1:mine" } };
        var incoming = new List<DNSRecord>
        {
            new() { Id = "a", Provider = DNSProviderKind.Cloudflare, Password = "enc:v1:mine", Login = "enc:v1:copied" },
            new() { Id = "b", Provider = DNSProviderKind.Cloudflare, Password = "enc:v1:mine" }
        };
        CredentialEncryption.DropForeignCiphertext(incoming, stored);
        Assert.Equal("enc:v1:mine", incoming[0].Password);
        Assert.Equal(string.Empty, incoming[0].Login);
        Assert.Equal(string.Empty, incoming[1].Password);
    }

    [Fact]
    public void Acme_NoNewOrderWithinADayOfOne()
    {
        var names = AcmePlanner.Names(new[] { "media.example.com" });
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = AcmeState.Succeeded(now.AddMinutes(-1), names, "https://acme.example/directory");

        // A certificate ordered a minute ago that already looks due for renewal is held back a day; an expired or
        // missing one is not, since it needs replacing now.
        Assert.Equal(AcmeAction.Wait, AcmePlanner.Decide(state, names, names, now.AddDays(20), now).Action);
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, names, names, now.AddDays(-1), now).Action);
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, names, Array.Empty<string>(), null, now).Action);

        // A day later it may be ordered again, and different names never wait.
        Assert.Equal(AcmeAction.Renew, AcmePlanner.Decide(state, names, names, now.AddDays(20), now.AddHours(25)).Action);
        var other = AcmePlanner.Names(new[] { "media.example.com", "app.example.com" });
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, other, names, now.AddDays(20), now).Action);
    }

    [Fact]
    public void Acme_FutureFailureTime_WaitsNoLongerThanTheBackoff()
    {
        var names = AcmePlanner.Names(new[] { "media.example.com" });
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = AcmePlanner.Normalize(new AcmeState().Failed(now.AddDays(365), "clock was wrong", names), now);
        var decision = AcmePlanner.Decide(state, names, Array.Empty<string>(), null, now);
        Assert.Equal(AcmeAction.Wait, decision.Action);
        Assert.True(decision.NextAttemptUtc <= now + AcmePlanner.Backoff(1));

        // Normalized once and stored, the wait ends: hourly checks over two days reach an attempt.
        var later = Enumerable.Range(1, 48).Select(h => now.AddHours(h)).First(t => AcmePlanner.Decide(state, names, Array.Empty<string>(), null, t).Action == AcmeAction.Issue);
        Assert.True(later <= now + AcmePlanner.Backoff(1) + TimeSpan.FromHours(1));
    }

    [Fact]
    public void Acme_FastClock_RenewsByTheCertificatesOwnLifetime()
    {
        var names = AcmePlanner.Names(new[] { "media.example.com" });

        // The server clock runs 70 days fast. Let's Encrypt issued a 90-day certificate at real time; this server
        // recorded the success on its own clock.
        var fast = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var notBefore = fast.AddDays(-70);
        var notAfter = notBefore.AddDays(90);
        var state = AcmeState.Succeeded(fast, names, "https://acme.example/directory");
        Assert.Equal(AcmeAction.None, AcmePlanner.Decide(state, names, names, notAfter, fast.AddDays(2), notBefore).Action);
        Assert.Equal(AcmeAction.None, AcmePlanner.Decide(state, names, names, notAfter, fast.AddDays(59), notBefore).Action);
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, names, names, notAfter, fast.AddDays(61), notBefore).Action);
    }

    [Fact]
    public void Gate_SignInSlots_CappedPerSlash56AndSlash48()
    {
        var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime()), Array.Empty<IConnectionFilter>());

        // Four /64s in one /56 fill it; a fifth is refused.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(gate.TryEnterSignIn(IPAddress.Parse($"2001:db8:1:{i}::1")));
        }

        Assert.False(gate.TryEnterSignIn(IPAddress.Parse("2001:db8:1:4::1")));

        // Other /56s of the same /48 share eight in all.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(gate.TryEnterSignIn(IPAddress.Parse($"2001:db8:1:{0x100 + i:x}::1")));
        }

        Assert.False(gate.TryEnterSignIn(IPAddress.Parse("2001:db8:1:200::1")));
        Assert.True(gate.TryEnterSignIn(IPAddress.Parse("2001:db8:2::1")));
    }

    [Fact]
    public void Gate_Slash48_CappedBelowTheWholeLimit()
    {
        var gate = new ConnectionGate(new ProxyRuntimeHolder(Runtime(s => { s.Advanced.MaxConnectionsPerIp = 10; s.Advanced.MaxConnections = 100; })), Array.Empty<IConnectionFilter>());
        var never = new TaskCompletionSource();
        var accepted = Enumerable.Range(0, 60)
            .Select(i => gate.OnConnectionAsync(new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse($"2001:db8:7:{i:x}00::1"), 1) }, _ => never.Task))
            .Count(t => !t.IsCompleted);
        Assert.Equal(25, accepted);
        never.SetResult();
    }

    [Fact]
    public void Bans_WiderBanFoundByAnyAddressInIt()
    {
        for (var net = 0; net < 4; net++)
        {
            for (var i = 0; i < 4; i++)
            {
                _bans.RecordFailure(IPAddress.Parse($"2001:db8:0:1{net}::{i + 1}"), Options());
            }
        }

        Assert.Equal("2001:db8::/56", _bans.BanFor(IPAddress.Parse("2001:db8:0:ab::9")));
        Assert.Null(_bans.BanFor(IPAddress.Parse("2001:db8:0:1ab::9")));
    }

    [Fact]
    public void Settings_WithCharactersXmlCannotHold_AreFound()
    {
        var config = new Jellyfin.Plugin.DynamicDns.Configuration.PluginConfiguration();
        config.Records.Add(new DNSRecord { Hostname = "ok.example.com" });
        Assert.Null(XmlText.FindInvalid(config));
        config.Records.Add(new DNSRecord { Hostname = "bad\u0001.example.com" });
        Assert.Equal("Records[1].Hostname", XmlText.FindInvalid(config));
        config.Records.RemoveAt(1);
        config.Proxy.Routes.Add(new ProxyRoute { Name = "x\uFFFE" });
        Assert.Equal("Proxy.Routes[0].Name", XmlText.FindInvalid(config));
    }

    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::a9fe:a9fe")]
    [InlineData("::a9fe:a9fe")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("fd20:ce::254")]
    [InlineData("168.63.129.16")]
    public void Metadata_TranslatedAndCloudForms_AreRefused(string address)
        => Assert.True(ProxySettingsValidator.IsMetadataAddress(IPAddress.Parse(address)));

    [Fact]
    public void HopMarker_CountsPassesAndKeepsOtherInstances()
    {
        Assert.Equal(0, ProxyPolicyMiddleware.HopsOf(null));
        var twice = ProxyPolicyMiddleware.NextHop(ProxyPolicyMiddleware.NextHop("other:5"));
        Assert.Equal(2, ProxyPolicyMiddleware.HopsOf(twice));
        Assert.Contains("other:5", twice, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_KeepsAPublicUrlTheAdminSetSince()
    {
        var network = new NetworkConfiguration { PublishedServerUriBySubnet = new[] { "all=https://old.example.com" } };
        var settings = new ProxySettings { Hostnames = new List<string> { "media.example.com" } };
        var snapshot = NetworkTakeover.Capture(network);
        snapshot.WrittenExternal = NetworkTakeover.ExternalEntry(settings);
        NetworkTakeover.ApplyManaged(network, settings);
        Assert.Contains("external=https://media.example.com", network.PublishedServerUriBySubnet);

        network.PublishedServerUriBySubnet = new[] { "all=https://old.example.com", "external=https://admin.example.com" };
        NetworkTakeover.Restore(network, snapshot);
        Assert.Contains("external=https://admin.example.com", network.PublishedServerUriBySubnet);

        // Unchanged, the written entry is removed.
        var untouched = new NetworkConfiguration();
        var before = NetworkTakeover.Capture(untouched);
        before.WrittenExternal = NetworkTakeover.ExternalEntry(settings);
        NetworkTakeover.ApplyManaged(untouched, settings);
        NetworkTakeover.Restore(untouched, before);
        Assert.Empty(untouched.PublishedServerUriBySubnet);
    }

    [Fact]
    public void History_OneRotatingSlash64TakesOneSlot()
    {
        var recorder = new AccessRecorder();
        for (var i = 1; i <= 500; i++)
        {
            recorder.RecordRequest(IPAddress.Parse($"2001:db8:9:9::{i:x}"), "https://media.example.com", "curl", local: false);
        }

        recorder.RecordRequest(IPAddress.Parse("203.0.113.77"), "https://media.example.com", "curl", local: false);
        var seen = recorder.DrainSeen();
        Assert.Equal(2, seen.Count);
        Assert.Equal(500, seen.Single(v => v.Ip.StartsWith("2001", StringComparison.Ordinal)).Requests);

        // The address a client used is found by any address in its group.
        Assert.Equal("https://media.example.com", recorder.UrlFor("2001:db8:9:9::ffff"));
    }

    [Fact]
    public void QuickConnect_IsItsOwnSignInPath()
    {
        Assert.True(PathRules.IsQuickConnectSignIn("/Users/AuthenticateWithQuickConnect/"));
        Assert.False(PathRules.IsQuickConnectSignIn("/Users/AuthenticateByName"));
    }

    [Fact]
    public void PublicIPv6_IsGlobalUnicastOnly()
    {
        Assert.False(IPAddressClassifier.IsPublic(IPAddress.Parse("4000::1")));
        Assert.False(IPAddressClassifier.IsPublic(IPAddress.Parse("e000::1")));
        Assert.True(IPAddressClassifier.IsPublic(IPAddress.Parse("2a00:1450::1")));
    }

    [Fact]
    public void Refusals_AreCountedByRule_AndSummedUp()
    {
        var geo = new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance);
        var server = new ServerLocation(
            new Jellyfin.Plugin.DynamicDns.Services.IPDetectionService(Substitute.For<IHttpClientFactory>(), NullLogger<Jellyfin.Plugin.DynamicDns.Services.IPDetectionService>.Instance),
            new Jellyfin.Plugin.DynamicDns.Services.StatusStoreService(AppPaths(), NullLogger<Jellyfin.Plugin.DynamicDns.Services.StatusStoreService>.Instance),
            geo,
            NullLogger<ServerLocation>.Instance);
        var filter = new BlockFilter(geo, server, _bans, NullLogger<BlockFilter>.Instance);
        var runtime = Runtime(s => s.Advanced.BlockedAddresses = new List<string> { "203.0.113.0/24" });
        for (var i = 1; i <= 3; i++)
        {
            Assert.False(filter.Allow(IPAddress.Parse("203.0.113." + i), runtime));
        }

        var minute = filter.TakeTotals();
        Assert.Equal(3, minute.Address);

        // Checking an open connection again, after a rule change, closes it without counting it twice.
        Assert.False(filter.Allow(IPAddress.Parse("203.0.113.1"), runtime, record: false));
        Assert.Equal(0, filter.TakeTotals().Total);
        Assert.Equal(0, filter.TakeTotals().Total);
        Assert.Equal("3 by the block list", minute.Describe());
        Assert.Equal("1 by the country rules, 3 by the block list", (minute + new RefusalTotals(0, 0, 1)).Describe());
    }

    [Fact]
    public async Task LeftoverTxt_UnreadableCredentials_SkipThatOneAndCleanTheRest()
    {
        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.AcmeDirectory);
        new JPKribs.Jellyfin.Base.JsonFileStore<PendingTxtList>(paths.AcmeTxtCleanupFile).Save(new PendingTxtList
        {
            Records =
            {
                new PendingTxt { RecordId = "bad", Name = "_acme-challenge.a.example.com", Value = "v1", PublishedUtc = DateTime.UtcNow },
                new PendingTxt { RecordId = "good", Name = "_acme-challenge.b.example.com", Value = "v2", PublishedUtc = DateTime.UtcNow }
            }
        });

        // Encrypted under one key ring, read with another: the credentials no longer decrypt.
        static SecretProtector Ring(string dir) => new("t", NullLogger.Instance, StableSecretProtection.Build(dir, "t", NullLogger.Instance));
        var sealedPassword = Ring(Path.Join(_dir, "ring1")).Protect("hunter2");
        var provider = Substitute.For<Jellyfin.Plugin.DynamicDns.Providers.IDNSProvider, Jellyfin.Plugin.DynamicDns.Providers.IAcmeDnsProvider>();
        provider.Kind.Returns(DNSProviderKind.Cloudflare);
        var acme = (Jellyfin.Plugin.DynamicDns.Providers.IAcmeDnsProvider)provider;
        acme.RemoveTxtAsync(Arg.Any<DNSRecord>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>()).Returns(Task.FromResult(true));
        var service = new AcmeCertificateService(paths, new Http01ChallengeStore(), Substitute.For<IHttpClientFactory>(), NullLogger<AcmeCertificateService>.Instance, new[] { provider }, Ring(Path.Join(_dir, "ring2")));
        var records = new[]
        {
            new DNSRecord { Id = "bad", Provider = DNSProviderKind.Cloudflare, Password = sealedPassword },
            new DNSRecord { Id = "good", Provider = DNSProviderKind.Cloudflare, Password = "plain" }
        };

        await service.CleanUpLeftoverTxtAsync(records);

        var left = Assert.Single(new JPKribs.Jellyfin.Base.JsonFileStore<PendingTxtList>(paths.AcmeTxtCleanupFile).Load().Records);
        Assert.Equal("bad", left.RecordId);
        await acme.Received(1).RemoveTxtAsync(Arg.Any<DNSRecord>(), "_acme-challenge.b.example.com", "v2", Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public void PendingTxt_Forget_MatchesACopyReadBackFromDisk()
    {
        var list = new PendingTxtList { Records = { new PendingTxt { Name = "n", Value = "v" }, new PendingTxt { Name = "n", Value = "other" } } };
        Assert.Equal(1, list.Forget(new PendingTxt { Name = "n", Value = "v" }));
        Assert.Equal("other", Assert.Single(list.Records).Value);
    }

    [Fact]
    public void Planner_CorrectingAFutureTime_KeepsTheIssuedCertificatesStart()
    {
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var notBefore = now.AddDays(-5);
        var state = AcmeState.Succeeded(now.AddDays(3), new[] { "media.example.com" }, "https://acme/", notBefore);

        var normalized = AcmePlanner.Normalize(state, now);
        Assert.Equal(now, normalized.LastSuccessUtc);
        Assert.Equal(notBefore, normalized.IssuedNotBeforeUtc);
    }

    [Fact]
    public void Planner_AnOlderCertificatePutBack_IsNotHeldAsTheNewOne()
    {
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var names = new[] { "media.example.com" };
        var state = AcmeState.Succeeded(now.AddDays(-10), names, "https://acme/", now.AddDays(-10));

        // Issued ten days ago, but the loaded file is an older one that ends in twenty days.
        var decision = AcmePlanner.Decide(state, names, names, now.AddDays(20), now, now.AddDays(-70));
        Assert.Equal(AcmeAction.Renew, decision.Action);

        // The certificate from that issue is still held until two thirds of its life.
        Assert.Equal(AcmeAction.None, AcmePlanner.Decide(state, names, names, now.AddDays(80), now, now.AddDays(-10)).Action);
    }

    [Fact]
    public void CertificateStamp_FollowsLinksAndNoticesSize()
    {
        var a = Path.Join(_dir, "a.pem");
        var b = Path.Join(_dir, "b.pem");
        var link = Path.Join(_dir, "live.pem");
        File.WriteAllText(a, "one");
        File.WriteAllText(b, "two!");
        File.CreateSymbolicLink(link, a);
        var first = CertificateStore.FileStamp(link);

        // Repointed, as certbot does on renewal, while the link keeps its own time.
        File.Delete(link);
        File.CreateSymbolicLink(link, b);
        Assert.NotEqual(first, CertificateStore.FileStamp(link));

        // Rewritten with its old time kept: the size still differs.
        var time = File.GetLastWriteTimeUtc(b);
        var before = CertificateStore.FileStamp(b);
        File.WriteAllText(b, "three");
        File.SetLastWriteTimeUtc(b, time);
        Assert.NotEqual(before, CertificateStore.FileStamp(b));
        Assert.Equal("missing", CertificateStore.FileStamp(Path.Join(_dir, "none.pem")));
    }

    [Fact]
    public async Task LeftoverTxt_IsRemovedOnALaterRun()
    {
        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.AcmeDirectory);
        new JPKribs.Jellyfin.Base.JsonFileStore<PendingTxtList>(paths.AcmeTxtCleanupFile).Save(new PendingTxtList
        {
            Records = { new PendingTxt { RecordId = "r1", Name = "_acme-challenge.media.example.com", Value = "v", PublishedUtc = DateTime.UtcNow } }
        });
        var provider = Substitute.For<Jellyfin.Plugin.DynamicDns.Providers.IDNSProvider, Jellyfin.Plugin.DynamicDns.Providers.IAcmeDnsProvider>();
        provider.Kind.Returns(DNSProviderKind.Cloudflare);
        ((Jellyfin.Plugin.DynamicDns.Providers.IAcmeDnsProvider)provider)
            .RemoveTxtAsync(Arg.Any<DNSRecord>(), "_acme-challenge.media.example.com", "v", Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(false), Task.FromResult(true));
        var service = new AcmeCertificateService(paths, new Http01ChallengeStore(), Substitute.For<IHttpClientFactory>(), NullLogger<AcmeCertificateService>.Instance, new[] { provider });
        var records = new[] { new DNSRecord { Id = "r1", Provider = DNSProviderKind.Cloudflare } };

        // The provider fails the first time: the record stays listed and is tried again.
        await service.CleanUpLeftoverTxtAsync(records);
        Assert.Single(new JPKribs.Jellyfin.Base.JsonFileStore<PendingTxtList>(paths.AcmeTxtCleanupFile).Load().Records);
        await service.CleanUpLeftoverTxtAsync(records);
        Assert.Empty(new JPKribs.Jellyfin.Base.JsonFileStore<PendingTxtList>(paths.AcmeTxtCleanupFile).Load().Records);
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64")]
    [InlineData("2001:db8::/48", null)]
    [InlineData("nonsense", null)]
    public void BlockTarget_AddressOrSlash64(string text, string? written)
    {
        var parsed = Jellyfin.Plugin.DynamicDns.Api.BlockGuard.TryParseTarget(text, out var target);
        Assert.Equal(written is not null, parsed);
        if (parsed)
        {
            Assert.Equal(written, Jellyfin.Plugin.DynamicDns.Api.BlockGuard.Text(target));
            Assert.True(Jellyfin.Plugin.DynamicDns.Api.BlockGuard.NamesOnly(written!, target));
        }
    }

    [Fact]
    public void ProtectedFolder_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Join(_dir, "private");
        SecretFile.ProtectFolder(folder);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
    }

    [Fact]
    public void FolderAccess_NamesTheFolderAndTheFix_WhenAFileCannotBeOpened()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Join(_dir, "locked");
        Directory.CreateDirectory(folder);
        var key = Path.Join(folder, "key.pem");
        File.WriteAllText(key, "secret");
        Assert.Equal(string.Empty, FolderAccess.Check(new[] { folder, Path.Join(_dir, "missing") }));

        File.SetUnixFileMode(key, UnixFileMode.None);
        try
        {
            var problem = FolderAccess.Check(new[] { folder });
            Assert.Contains(folder, problem, StringComparison.Ordinal);
            Assert.Contains("chown -R", problem, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            FolderAccess.Check(Array.Empty<string>());
        }
    }

    [Theory]
    [InlineData(CountryRuleMode.ServerCountry, 0, "United States", "Only visitors from United States can connect")]
    [InlineData(CountryRuleMode.ServerCountry, 2, "United States", "and 2 other countries")]
    [InlineData(CountryRuleMode.ServerCountry, 0, "", "once its country is known")]
    [InlineData(CountryRuleMode.AllowOnly, 3, "United States", "3 chosen countries")]
    [InlineData(CountryRuleMode.Block, 1, "United States", "Visitors from 1 country are refused")]
    [InlineData(CountryRuleMode.Off, 0, "United States", "")]
    public void CountryRule_IsDescribedInWords(CountryRuleMode mode, int countries, string server, string expected)
    {
        var options = ProxyOptions.Standard with { CountryMode = mode, Countries = Enumerable.Repeat("CA", countries).ToList() };
        var text = ProxyManager.DescribeCountryRule(options, server);
        if (expected.Length == 0)
        {
            Assert.Equal(string.Empty, text);
        }
        else
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KeyRing_LeftAloneWhereTheSystemHasNoKeyStore()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Join(_dir, "keys");
        Directory.CreateDirectory(folder);
        var key = Path.Join(folder, "key-1.xml");
        File.WriteAllText(key, "<key><masterKey xmlns:p4='http://schemas.asp.net/2015/03/dataProtection' p4:requiresEncryption='true'><value>x</value></masterKey></key>");
        Assert.Equal(0, KeyRingProtection.Protect(folder, NullLogger.Instance));
        Assert.Contains("<value>x</value>", File.ReadAllText(key), StringComparison.Ordinal);
    }

    [Fact]
    public void SelfSigned_IsIssuedFromAStableRoot()
    {
        var rootPath = Path.Join(_dir, "root.pfx");
        using var root = LocalAuthority.LoadOrCreateRoot(rootPath, out var created);
        Assert.True(created);
        var constraints = root.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.CertificateAuthority);

        // Loaded again, it is the same root, so devices that installed it keep trusting what it issues.
        using var again = LocalAuthority.LoadOrCreateRoot(rootPath, out var createdAgain);
        Assert.False(createdAgain);
        Assert.Equal(root.Thumbprint, again.Thumbprint);
        Assert.Equal(LocalAuthority.Fingerprint(root), LocalAuthority.Fingerprint(again));

        using var leaf = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(LocalAuthority.Issue(root, new[] { "media.example.com", "localhost" }), null);
        Assert.True(leaf.HasPrivateKey);
        Assert.True(LocalAuthority.IssuedBy(leaf, again));
        Assert.True((leaf.NotAfter - leaf.NotBefore).TotalDays <= 399);
        Assert.False(leaf.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().Single().CertificateAuthority);

        // A certificate from some other root is not taken for one of ours.
        using var other = LocalAuthority.LoadOrCreateRoot(Path.Join(_dir, "other.pfx"), out _);
        Assert.False(LocalAuthority.IssuedBy(leaf, other));
    }

    [Fact]
    public void SelfSigned_StoreServesARootIssuedCertificate_AndOffersTheRoot()
    {
        var paths = new ProxyPaths(_dir);
        using var store = new CertificateStore(paths, new JPKribs.Jellyfin.Base.SecretProtector("test", NullLogger.Instance), NullLogger<CertificateStore>.Instance);
        store.Configure(new ProxyCertificateSettings { Mode = CertificateMode.SelfSigned }, new[] { "media.example.com" });
        Assert.True(store.Status.Loaded, store.Status.Error);
        Assert.Contains("Remote Access root", store.Status.Issuer, StringComparison.Ordinal);
        var root = Assert.IsType<RootCertificate>(store.Root);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", root.Pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", root.Pem, StringComparison.Ordinal);
    }

    private MediaBrowser.Common.Configuration.IApplicationPaths AppPaths()
    {
        var paths = Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_dir);
        return paths;
    }

    private sealed class Switch : IConnectionFilter
    {
        public bool Refuse { get; set; }

        public bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true) => !Refuse;
    }
}
