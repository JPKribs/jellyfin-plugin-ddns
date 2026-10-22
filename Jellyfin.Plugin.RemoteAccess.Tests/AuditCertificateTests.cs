using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Certes;
using Certes.Acme;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Acme;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Fixes from the certificate and network audit: the startup fallback for Only through the proxy, serialized
/// trust changes, restarts that can be asked for again, published addresses that follow the hostnames, and the
/// Let's Encrypt timing and failure rules.
/// </summary>
public sealed class AuditCertificateTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] _names = { "media.example.com" };

    private readonly string _dir = Path.Join(Path.GetTempPath(), "audit-cert-" + Guid.NewGuid().ToString("N"));
    private readonly IConfigurationManager _configuration = Substitute.For<IConfigurationManager>();
    private readonly ISystemManager _system = Substitute.For<ISystemManager>();
    private readonly List<ActivityLog> _activity = new();

    public AuditCertificateTests()
    {
        Directory.CreateDirectory(_dir);
        _configuration.CommonApplicationPaths.ConfigurationDirectoryPath.Returns(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open is left for the system's temporary-file cleanup.
        }
    }

    [Fact]
    public async Task Startup_BoundToLoopbackAndProxyDown_GivesJellyfinItsAddressesBack()
    {
        var network = Network();
        Serve(network);
        using (var before = Orchestrator(started: false))
        {
            before.Commit(Settings(true), network);
        }

        Assert.Equal(new[] { "127.0.0.1" }, network.LocalNetworkAddresses);

        // After the restart, the proxy could not bind its port.
        using var orchestrator = Orchestrator(started: true);
        await orchestrator.OnStartupCoreAsync(_ => Task.FromResult<string?>("Port 443 is already in use by another program."), () => Settings(true), CancellationToken.None);

        Assert.Contains("192.168.1.10", network.LocalNetworkAddresses);
        Assert.DoesNotContain(network.PublishedServerUriBySubnet, e => e.StartsWith("0.0.0.0/0=", StringComparison.Ordinal));
        Assert.Contains("external=https://media.example.com", network.PublishedServerUriBySubnet);
        var state = orchestrator.State;
        Assert.Equal(TakeoverPhase.Managed, state.Phase);
        Assert.False(state.Snapshot!.BoundToLoopback);
        Assert.Empty(state.Snapshot.WrittenLocal);
        Assert.Contains("already in use", state.LastError, StringComparison.Ordinal);
        Assert.False(orchestrator.IsBoundToLoopback);

        await Eventually(() => _system.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISystemManager.Restart)));
        await Eventually(() => Activity().Any(a => a.Type == "DynamicDNS.TakeoverLoopbackReleased"));
        var entry = Activity().First(a => a.Type == "DynamicDNS.TakeoverLoopbackReleased");
        Assert.Equal(LogLevel.Error, entry.LogSeverity);
        AssertPlain(entry.Name);
        AssertPlain(entry.ShortOverview);

        // Undo still puts the original settings back, the trust included.
        orchestrator.Restore("test", restart: false);
        Assert.Equal(new[] { "192.168.1.10" }, network.LocalNetworkAddresses);
        Assert.Empty(network.KnownProxies);
    }

    [Fact]
    public async Task Startup_NotBoundAndProxyDown_OnlyReports()
    {
        var network = Network();
        Serve(network);
        using (var before = Orchestrator(started: false))
        {
            before.Commit(Settings(false), network);
        }

        var bound = network.LocalNetworkAddresses.ToArray();
        using var orchestrator = Orchestrator(started: true);
        await orchestrator.OnStartupCoreAsync(_ => Task.FromResult<string?>("The proxy is not running."), () => Settings(false), CancellationToken.None);

        Assert.Equal(bound, network.LocalNetworkAddresses);
        Assert.Equal("The proxy is not running.", orchestrator.State.LastError);
        await Task.Delay(100);
        _system.DidNotReceive().Restart();
    }

    [Fact]
    public async Task Startup_BoundAndProxyWorks_KeepsTheBinding()
    {
        var network = Network();
        Serve(network);
        using (var before = Orchestrator(started: false))
        {
            before.Commit(Settings(true), network);
        }

        using var orchestrator = Orchestrator(started: true);
        await orchestrator.OnStartupCoreAsync(_ => Task.FromResult<string?>(null), () => Settings(true), CancellationToken.None);

        Assert.Equal(new[] { "127.0.0.1" }, network.LocalNetworkAddresses);
        Assert.True(orchestrator.IsBoundToLoopback);
        Assert.Empty(orchestrator.State.LastError);
    }

    [Fact]
    public async Task Commit_OverlappingCommits_KeepTheOriginalValuesInTheSnapshot()
    {
        var network = Network();
        using var orchestrator = Orchestrator(started: false);
        using var barrier = new Barrier(8);
        var commits = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return orchestrator.Commit(Settings(i % 2 == 0), network);
        })).ToArray();
        await Task.WhenAll(commits);

        var snapshot = orchestrator.State.Snapshot!;
        Assert.False(snapshot.EnableRemoteAccess);
        Assert.Equal(new[] { "192.168.1.10" }, snapshot.LocalNetworkAddresses);
        Assert.Empty(snapshot.KnownProxies);
        Assert.Empty(snapshot.PublishedServerUriBySubnet);
    }

    [Fact]
    public void Commit_CapturesTheSavedFileNotTheAlteredInstance()
    {
        // Jellyfin's saved settings, as the administrator left them.
        var saved = Network();
        using (var file = File.Create(Path.Join(_dir, "network.xml")))
        {
            new XmlSerializer(typeof(NetworkConfiguration)).Serialize(file, saved);
        }

        // The instance in memory was already changed, as an earlier change in this run would leave it.
        var cached = Network();
        cached.EnableRemoteAccess = true;
        cached.KnownProxies = new[] { "127.0.0.1", "::1" };
        cached.LocalNetworkAddresses = new[] { "127.0.0.1" };

        using var orchestrator = Orchestrator(started: false);
        orchestrator.Commit(Settings(false), cached);

        var snapshot = orchestrator.State.Snapshot!;
        Assert.False(snapshot.EnableRemoteAccess);
        Assert.Empty(snapshot.KnownProxies);
        Assert.Equal(new[] { "192.168.1.10" }, snapshot.LocalNetworkAddresses);
    }

    [Fact]
    public void Commit_UnreadableState_IsRefusedRatherThanTreatedAsUnmanaged()
    {
        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.StateFile, "{ \"Phase\": 2, \"Snapshot\": { trunc");

        using var orchestrator = Orchestrator(started: false);
        var network = Network();
        var result = orchestrator.Commit(Settings(false), network);

        Assert.False(result.Success);
        Assert.Contains("could not read", result.Message, StringComparison.Ordinal);
        AssertPlain(result.Message.Replace(paths.StateFile, "the file", StringComparison.Ordinal));
        _configuration.DidNotReceive().SaveConfiguration("network", Arg.Any<object>());
        Assert.StartsWith("{ \"Phase\": 2", File.ReadAllText(paths.StateFile), StringComparison.Ordinal);
        Assert.False(orchestrator.Restore("test", restart: false).Success);
    }

    [Fact]
    public async Task Restart_AFailedRestart_DoesNotBlockTheNextOne()
    {
        var network = Network();
        Serve(network);
        var calls = 0;
        _system.When(s => s.Restart()).Do(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("not now");
            }
        });

        using var orchestrator = Orchestrator(started: true);
        orchestrator.Commit(Settings(false), network);
        await Eventually(() => Volatile.Read(ref calls) == 1);
        await Eventually(() => Activity().Any(a => a.Type == "DynamicDNS.RestartFailed"));

        orchestrator.Restore("test", restart: true);
        await Eventually(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task UpdatePublishedAddresses_FollowsNewHostnamesWithoutARestart()
    {
        var network = Network();
        Serve(network);
        using var orchestrator = Orchestrator(started: false);
        orchestrator.Commit(Settings(false), network);
        await orchestrator.OnStartupCoreAsync(_ => Task.FromResult<string?>(null), () => Settings(false), CancellationToken.None);
        Assert.Contains("external=https://media.example.com", network.PublishedServerUriBySubnet);

        var renamed = Settings(false);
        renamed.Hostnames = new List<string> { "watch.example.com" };
        Assert.True(orchestrator.UpdatePublishedAddresses(renamed));
        Assert.Contains("external=https://watch.example.com", network.PublishedServerUriBySubnet);
        Assert.DoesNotContain("external=https://media.example.com", network.PublishedServerUriBySubnet);
        Assert.Equal("external=https://watch.example.com", orchestrator.State.Snapshot!.WrittenExternal);
        Assert.Equal(TakeoverPhase.Managed, orchestrator.State.Phase);

        // Nothing changed, nothing written. A wildcard alone keeps the earlier entry.
        Assert.False(orchestrator.UpdatePublishedAddresses(renamed));
        var wildcard = Settings(false);
        wildcard.Hostnames = new List<string> { "*.example.com" };
        Assert.False(orchestrator.UpdatePublishedAddresses(wildcard));

        // Undo removes the entry it wrote last.
        orchestrator.Restore("test", restart: false);
        Assert.DoesNotContain(network.PublishedServerUriBySubnet, e => e.StartsWith("external=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdatePublishedAddresses_WhileBound_ReplacesTheEntriesForEveryClient()
    {
        var network = Network();
        Serve(network);
        using var orchestrator = Orchestrator(started: false);
        orchestrator.Commit(Settings(true), network);
        await orchestrator.OnStartupCoreAsync(_ => Task.FromResult<string?>(null), () => Settings(true), CancellationToken.None);

        var renamed = Settings(true);
        renamed.Hostnames = new List<string> { "watch.example.com" };
        Assert.True(orchestrator.UpdatePublishedAddresses(renamed));
        Assert.Equal(
            new[] { "0.0.0.0/0=https://watch.example.com", "::/0=https://watch.example.com", "external=https://watch.example.com" },
            network.PublishedServerUriBySubnet);
        Assert.Equal(new[] { "0.0.0.0/0=https://watch.example.com", "::/0=https://watch.example.com" }, orchestrator.State.Snapshot!.WrittenLocal);
    }

    [Fact]
    public void UpdatePublishedAddresses_DoesNothingWhileUnmanaged()
    {
        using var orchestrator = Orchestrator(started: false);
        Assert.False(orchestrator.UpdatePublishedAddresses(Settings(false)));
        _configuration.DidNotReceive().SaveConfiguration("network", Arg.Any<object>());
    }

    [Fact]
    public async Task WaitForTxt_ALookupThatHangs_TimesOutAndTheWaitGoesOn()
    {
        var calls = 0;
        using var service = Service(new HttpClient(new Hang(() => Interlocked.Increment(ref calls))));
        service.TxtPolls = 3;
        service.TxtPollDelay = TimeSpan.FromMilliseconds(1);
        service.TxtLookupTimeout = TimeSpan.FromMilliseconds(50);

        await service.WaitForTxtAsync("_acme-challenge.media.example.com", "value", CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitForTxt_TheClientsOwnTimeout_IsAFailedLookupNotACancelledRun()
    {
        using var service = Service(new HttpClient(new Hang(() => { })) { Timeout = TimeSpan.FromMilliseconds(30) });
        service.TxtPolls = 2;
        service.TxtPollDelay = TimeSpan.FromMilliseconds(1);
        service.TxtLookupTimeout = TimeSpan.FromSeconds(30);

        await service.WaitForTxtAsync("_acme-challenge.media.example.com", "value", CancellationToken.None);
    }

    [Fact]
    public async Task WaitForTxt_TheCallersCancellation_StillStopsIt()
    {
        using var service = Service(new HttpClient(new Hang(() => { })));
        service.TxtPolls = 100;
        service.TxtPollDelay = TimeSpan.FromMilliseconds(1);
        service.TxtLookupTimeout = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitForTxtAsync("_acme-challenge.media.example.com", "value", cancel.Token));
    }

    [Fact]
    public void Planner_HoldsAfterAnySuccessForTheSameNamesAndDirectory()
    {
        const string Directory = "https://acme.example/directory";
        var state = AcmeState.Succeeded(_now.AddHours(-2), _names, Directory, _now.AddHours(-3));

        // The issued certificate never loaded, so nothing covers the names: still held for a day.
        var decision = AcmePlanner.Decide(state, _names, Array.Empty<string>(), null, _now, null, Directory);
        Assert.Equal(AcmeAction.Wait, decision.Action);
        Assert.Equal(_now.AddHours(22), decision.NextAttemptUtc);

        // Loaded but covering other names, or already expired by a wrong clock: still held.
        Assert.Equal(AcmeAction.Wait, AcmePlanner.Decide(state, _names, new[] { "other.example.com" }, _now.AddDays(60), _now, null, Directory).Action);
        Assert.Equal(AcmeAction.Wait, AcmePlanner.Decide(state, _names, _names, _now.AddDays(-1), _now, null, Directory).Action);

        // After the day, or for other names, or from another directory, it is ordered.
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, _names, Array.Empty<string>(), null, _now.AddHours(23), null, Directory).Action);
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, new[] { "a.example.com", "media.example.com" }, Array.Empty<string>(), null, _now, null, Directory).Action);
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(state, _names, Array.Empty<string>(), null, _now, null, "https://staging.example/directory").Action);
    }

    [Fact]
    public void Planner_NextCheck_IsTheRenewalTimeAfterASuccess()
    {
        var notBefore = _now.AddDays(-10);
        var notAfter = notBefore.AddDays(90);
        var state = AcmeState.Succeeded(notBefore, _names, "https://acme.example/directory", notBefore);

        // Two thirds into its 90 days, counted from the issue.
        Assert.Equal(notBefore.AddDays(60), AcmePlanner.NextCheckUtc(state, _names, _names, notAfter, _now, notBefore));

        // Within the day after it, the end of the hold. With nothing to order, nothing.
        Assert.Equal(notBefore.AddDays(1), AcmePlanner.NextCheckUtc(state, _names, Array.Empty<string>(), null, notBefore.AddHours(1)));
        Assert.Null(AcmePlanner.NextCheckUtc(state, AcmePlanner.Names(new[] { "*.example.com" }), _names, notAfter, _now, notBefore));

        // A certificate from elsewhere renews thirty days before it expires. One due is due now.
        Assert.Equal(_now.AddDays(50), AcmePlanner.NextCheckUtc(new AcmeState(), _names, _names, _now.AddDays(80), _now));
        Assert.Equal(_now, AcmePlanner.NextCheckUtc(new AcmeState(), _names, Array.Empty<string>(), null, _now));
    }

    [Fact]
    public void State_AFailureKeepsWhatIsKnownAboutTheIssuedCertificate()
    {
        var issued = AcmeState.Succeeded(_now, _names, "https://acme.example/directory", _now.AddHours(-1));
        var failed = issued.Failed(_now.AddDays(70), "Connection refused", _names, rateLimited: true);

        Assert.Equal("https://acme.example/directory", failed.Directory);
        Assert.Equal(_now.AddHours(-1), failed.IssuedNotBeforeUtc);
        Assert.Equal(_now, failed.LastSuccessUtc);
        Assert.True(failed.RateLimited);
        Assert.False(failed.Failed(_now.AddDays(71), "Connection refused", _names).RateLimited);
    }

    [Fact]
    public void RateLimit_IsRecognisedAndHoldsUntilTheBackoffEnds()
    {
        var limited = new AcmeRequestException("too many", new AcmeError { Status = HttpStatusCode.TooManyRequests, Detail = "too many certificates" });
        Assert.True(AcmeCertificateService.IsRateLimit(limited));
        Assert.False(AcmeCertificateService.IsRateLimit(new AcmeRequestException("bad", new AcmeError { Status = HttpStatusCode.BadRequest, Detail = "bad" })));
        Assert.False(AcmeCertificateService.IsRateLimit(new HttpRequestException("down")));

        var state = new AcmeState().Failed(_now, "too many certificates", _names, rateLimited: true);
        Assert.True(AcmeCertificateService.InRateLimit(state, _now.AddMinutes(30)));
        Assert.False(AcmeCertificateService.InRateLimit(state, _now.AddHours(2)));
        Assert.False(AcmeCertificateService.InRateLimit(new AcmeState().Failed(_now, "refused", _names), _now.AddMinutes(30)));
    }

    [Fact]
    public async Task Force_WaitsOutARateLimit()
    {
        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.AcmeDirectory);
        var limited = AcmePlanner.Names(_names);
        var failedAt = DateTime.UtcNow.AddMinutes(-5);
        new JsonFileStore<AcmeState>(paths.AcmeStateFile).Save(new AcmeState().Failed(failedAt, "too many certificates", limited, rateLimited: true));

        var before = Environment.GetEnvironmentVariable("DDNS_ACME_DIRECTORY");
        Environment.SetEnvironmentVariable("DDNS_ACME_DIRECTORY", "http://127.0.0.1:1/directory");
        try
        {
            using var service = Service(new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
            Assert.False(await service.EnsureAsync(_names, Array.Empty<Models.DNSRecord>(), new CertificateStatus(), CancellationToken.None, force: true));

            // No attempt was made: the record is the one from before.
            var after = new JsonFileStore<AcmeState>(paths.AcmeStateFile).Load();
            Assert.Equal(failedAt, after.LastAttemptUtc);
            Assert.Equal(1, after.ConsecutiveFailures);
            var status = service.Describe(_names, Array.Empty<Models.DNSRecord>());
            Assert.True(status.RateLimited);
            Assert.True(status.NextAttemptUtc > DateTime.UtcNow);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DDNS_ACME_DIRECTORY", before);
        }
    }

    [Fact]
    public void AccountKey_IsCreatedOnlyWhenThereIsNone()
    {
        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.AcmeDirectory);
        using var service = Service(new HttpClient());

        var created = service.LoadOrCreateAccountKey().ToPem();
        Assert.Equal(created, service.LoadOrCreateAccountKey().ToPem());

        // A damaged key is set aside, not lost, and replaced.
        File.WriteAllText(paths.AcmeAccountKeyFile, "-----BEGIN EC PRIVATE KEY-----\nnot a key\n-----END EC PRIVATE KEY-----\n");
        var replaced = service.LoadOrCreateAccountKey().ToPem();
        Assert.NotEqual(created, replaced);
        Assert.Single(Directory.GetFiles(paths.AcmeDirectory, "account.pem.damaged-*"));
    }

    [Fact]
    public void AccountKey_ThatCannotBeReadNow_FailsTheAttemptAndIsKept()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new ProxyPaths(_dir);
        Directory.CreateDirectory(paths.AcmeDirectory);
        using var service = Service(new HttpClient());
        var created = service.LoadOrCreateAccountKey().ToPem();

        File.SetUnixFileMode(paths.AcmeAccountKeyFile, UnixFileMode.None);
        try
        {
            if (CanRead(paths.AcmeAccountKeyFile))
            {
                // Running as a user that reads everything, so the case cannot be made here.
                return;
            }

            var ex = Assert.Throws<IOException>(() => service.LoadOrCreateAccountKey());
            AssertPlain(ex.Message);
        }
        finally
        {
            File.SetUnixFileMode(paths.AcmeAccountKeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(created, service.LoadOrCreateAccountKey().ToPem());
    }

    [Fact]
    public void IssuedFiles_ThatDoNotLoad_AreReported()
    {
        var path = Path.Join(_dir, "chain.pem");
        File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");
        Assert.NotNull(CertificateStore.PemLoadError(path));
        Assert.NotNull(CertificateStore.PemLoadError(Path.Join(_dir, "missing.pem")));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=media.example.com", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());
        Assert.Null(CertificateStore.PemLoadError(path));
    }

    [Theory]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", true)]
    [InlineData("0F1E2D3C4B5A69788796A5B4C3D2E1F0", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", true)]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("", "", false)]
    [InlineData(null, "0f1e2d3c4b5a69788796a5b4c3d2e1f0", false)]
    public void SameId_IgnoresDashesAndCase(string? a, string? b, bool same)
        => Assert.Equal(same, ProxyManager.SameId(a, b));

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // On-screen text is short plain sentences.
    private static void AssertPlain(string? text)
    {
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain(";", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(")", text, StringComparison.Ordinal);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    private static ProxySettings Settings(bool loopbackOnly) => new()
    {
        Enabled = true,
        Hostnames = new List<string> { "media.example.com" },
        Advanced = new ProxyAdvancedSettings { JellyfinLoopbackOnly = loopbackOnly }
    };

    private static NetworkConfiguration Network() => new() { EnableRemoteAccess = false, EnableIPv4 = true, EnableIPv6 = false, LocalNetworkAddresses = new[] { "192.168.1.10" } };

    private List<ActivityLog> Activity()
    {
        lock (_activity)
        {
            return _activity.ToList();
        }
    }

    // Jellyfin hands out the same instance every time, as its configuration manager does.
    private void Serve(NetworkConfiguration network) => _configuration.GetConfiguration("network").Returns(network);

    private JellyfinNetworkOrchestrator Orchestrator(bool started)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(new CancellationToken(started));
        var manager = Substitute.For<IActivityManager>();
        manager.CreateAsync(Arg.Do<ActivityLog>(e =>
        {
            lock (_activity)
            {
                _activity.Add(e);
            }
        }));
        return new JellyfinNetworkOrchestrator(
            _configuration,
            _system,
            lifetime,
            new ProxyPaths(_dir),
            new ActivityLogger(manager, Substitute.For<ILogger<ActivityLogger>>()),
            NullLogger<JellyfinNetworkOrchestrator>.Instance)
        {
            RestartDelay = TimeSpan.Zero
        };
    }

    private AcmeCertificateService Service(HttpClient client)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(client);
        return new AcmeCertificateService(new ProxyPaths(_dir), new Http01ChallengeStore(), http, NullLogger<AcmeCertificateService>.Instance);
    }

    // A server that never answers until the request is cancelled.
    private sealed class Hang : HttpMessageHandler
    {
        private readonly Action _called;

        public Hang(Action called) => _called = called;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _called();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
