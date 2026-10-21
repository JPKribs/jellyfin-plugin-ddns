using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Acme;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Automatic certificates: when Let's Encrypt is asked, how failures back off, and how the issued files load.
/// A real issuance needs a public address with port 80 forwarded, so it cannot run here; the failure path
/// runs against a closed local port instead.
/// </summary>
public sealed class AcmeTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] _names = { "media.example.com" };
    private static readonly DNSRecord[] NoRecords = Array.Empty<DNSRecord>();

    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-acme-" + Guid.NewGuid().ToString("N"));

    public AcmeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open is left for the system's temporary-file cleanup.
        }
    }

    [Fact]
    public void Names_DropWildcardsAndSort()
        => Assert.Equal(
            new[] { "a.example.com", "media.example.com" },
            AcmePlanner.Names(new[] { "Media.Example.com", "*.example.com", "a.example.com", "media.example.com", " " }));

    [Fact]
    public void Decide_NoNamesWhenOnlyWildcards()
        => Assert.Equal(AcmeAction.NoNames, AcmePlanner.Decide(new AcmeState(), AcmePlanner.Names(new[] { "*.example.com" }), Array.Empty<string>(), null, _now).Action);

    [Fact]
    public void Decide_IssuesWithoutACertificate()
        => Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(new AcmeState(), _names, Array.Empty<string>(), null, _now).Action);

    [Fact]
    public void Decide_IssuesWhenTheCertificateMissesAName()
        => Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(new AcmeState(), new[] { "a.example.com", "media.example.com" }, _names, _now.AddDays(60), _now).Action);

    [Fact]
    public void Decide_IssuesWhenExpired()
        => Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(new AcmeState(), _names, _names, _now.AddMinutes(-1), _now).Action);

    [Fact]
    public void Decide_RenewsWithinThirtyDays()
    {
        Assert.Equal(AcmeAction.Renew, AcmePlanner.Decide(new AcmeState(), _names, _names, _now.AddDays(29), _now).Action);
        Assert.Equal(AcmeAction.None, AcmePlanner.Decide(new AcmeState(), _names, _names, _now.AddDays(31), _now).Action);
    }

    [Fact]
    public void Decide_NamesCompareCaseInsensitively()
        => Assert.Equal(AcmeAction.None, AcmePlanner.Decide(new AcmeState(), _names, new[] { "MEDIA.example.com" }, _now.AddDays(60), _now).Action);

    [Fact]
    public void Decide_WaitsOutTheBackoffThenRetries()
    {
        var failed = new AcmeState().Failed(_now, "Connection refused", _names).Failed(_now, "Connection refused", _names);
        Assert.Equal(2, failed.ConsecutiveFailures);

        var wait = AcmePlanner.Decide(failed, _names, Array.Empty<string>(), null, _now.AddMinutes(90));
        Assert.Equal(AcmeAction.Wait, wait.Action);
        Assert.Equal(_now.AddHours(2), wait.NextAttemptUtc);

        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(failed, _names, Array.Empty<string>(), null, _now.AddHours(2)).Action);
    }

    [Fact]
    public void Decide_RetriesAtOnceWhenTheNamesChange()
    {
        var failed = new AcmeState().Failed(_now, "Wrong address", new[] { "old.example.com" });
        Assert.Equal(AcmeAction.Issue, AcmePlanner.Decide(failed, _names, Array.Empty<string>(), null, _now.AddMinutes(1)).Action);

        // A failure for the new names starts a fresh streak rather than adding to the old one.
        Assert.Equal(1, failed.Failed(_now, "Still wrong", _names).ConsecutiveFailures);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 24)]
    [InlineData(40, 24)]
    public void Backoff_DoublesFromAnHourToADay(int failures, int hours)
        => Assert.Equal(TimeSpan.FromHours(hours), AcmePlanner.Backoff(failures));

    [Fact]
    public void Success_ClearsTheStreak()
    {
        var state = AcmeState.Succeeded(_now, _names, "https://acme.example/directory");
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Empty(state.LastError);
        Assert.Equal(_now, state.LastSuccessUtc);
    }

    [Fact]
    public void Automatic_IsTheDefaultSourceButTheTermsStartUnaccepted()
    {
        var settings = new ProxyCertificateSettings();
        Assert.Equal(CertificateMode.Automatic, settings.Mode);
        Assert.False(settings.AcceptTerms);
    }

    [Fact]
    public void Store_AutomaticWaitsQuietlyThenLoadsTheIssuedFiles()
    {
        var paths = new ProxyPaths(_dir);
        using var store = new CertificateStore(paths, new SecretProtector("test", NullLogger.Instance), NullLogger<CertificateStore>.Instance);
        store.Configure(new ProxyCertificateSettings { Mode = CertificateMode.Automatic }, _names);

        // Nothing issued yet is not an error, just no HTTPS for now.
        Assert.False(store.HasCertificate);
        Assert.Empty(store.Status.Error);

        WriteIssuedFiles(paths, "media.example.com");
        store.Refresh();
        Assert.True(store.HasCertificate);
        Assert.Equal(_names, store.Status.Names);
        Assert.Empty(store.Status.UncoveredHostnames);
    }

    [Fact]
    public async Task Ensure_DoesNothingWhileTheCertificateIsCurrent()
    {
        var (service, entries) = Service();
        using (service)
        {
            var current = new CertificateStatus { Loaded = true, Names = new List<string>(_names), NotAfterUtc = DateTime.UtcNow.AddDays(60) };
            Assert.False(await service.EnsureAsync(_names, NoRecords, current, CancellationToken.None));
            Assert.False(await service.EnsureAsync(new[] { "*.example.com" }, NoRecords, new CertificateStatus(), CancellationToken.None));
            Assert.Empty(entries);
            Assert.False(File.Exists(new ProxyPaths(_dir).AcmeStateFile));
        }
    }

    [Fact]
    public async Task Ensure_RecordsAFailureAndBacksOff()
    {
        // A closed local port stands in for an unreachable Let's Encrypt.
        var before = Environment.GetEnvironmentVariable("DDNS_ACME_DIRECTORY");
        Environment.SetEnvironmentVariable("DDNS_ACME_DIRECTORY", "http://127.0.0.1:1/directory");
        try
        {
            var (service, entries) = Service();
            using (service)
            {
                Assert.False(await service.EnsureAsync(_names, NoRecords, new CertificateStatus(), CancellationToken.None));
                var status = service.Describe(_names, NoRecords);
                Assert.NotEmpty(status.LastError);
                Assert.False(status.Issuing);
                Assert.True(status.NextAttemptUtc > DateTime.UtcNow.AddMinutes(50));

                var entry = Assert.Single(entries);
                Assert.Equal("Remote Access could not get a certificate", entry.Name);
                Assert.Equal("DynamicDNS.CertificateIssueFailed", entry.Type);

                // Inside the backoff the next call does not try again.
                Assert.False(await service.EnsureAsync(_names, NoRecords, new CertificateStatus(), CancellationToken.None));
                Assert.Single(entries);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DDNS_ACME_DIRECTORY", before);
        }
    }

    [Fact]
    public void Plan_UsesDnsWhenEveryNameHasATxtCapableRecord()
    {
        var records = new[]
        {
            new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com" },
            new DNSRecord { Provider = DNSProviderKind.DuckDns, Hostname = "myhome" }
        };
        var plan = AcmePlan.For(new[] { "media.example.com", "*.example.com", "myhome.duckdns.org" }, records, CloudflareAndDuck);

        Assert.Equal(AcmeMethod.Dns, plan.Method);
        Assert.Equal(new[] { "*.example.com", "media.example.com", "myhome.duckdns.org" }, plan.Names);
        Assert.Equal(DNSProviderKind.Cloudflare, plan.Records["*.example.com"].Provider);
        Assert.Equal(DNSProviderKind.DuckDns, plan.Records["myhome.duckdns.org"].Provider);
    }

    [Fact]
    public void Plan_FallsBackToHttpWhenAnyNameIsUncovered()
    {
        var records = new[] { new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com" } };
        var plan = AcmePlan.For(new[] { "media.example.com", "*.example.com", "other.net" }, records, CloudflareAndDuck);

        Assert.Equal(AcmeMethod.Http, plan.Method);
        Assert.Equal(new[] { "media.example.com", "other.net" }, plan.Names);
        Assert.Empty(plan.Records);
    }

    [Fact]
    public void Plan_IgnoresDisabledRecordsAndProvidersWithoutTxt()
    {
        var disabled = new[] { new DNSRecord { Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Zone = "example.com", Enabled = false } };
        Assert.Equal(AcmeMethod.Http, AcmePlan.For(_names, disabled, CloudflareAndDuck).Method);

        var noTxt = new[] { new DNSRecord { Provider = DNSProviderKind.NoIp, Hostname = "media.example.com" } };
        Assert.Equal(AcmeMethod.Http, AcmePlan.For(_names, noTxt, CloudflareAndDuck).Method);
    }

    [Fact]
    public void Plan_PrefersTheMostSpecificRecord()
    {
        var records = new[]
        {
            new DNSRecord { Name = "zone", Provider = DNSProviderKind.Cloudflare, Hostname = "a.example.com", Zone = "example.com" },
            new DNSRecord { Name = "exact", Provider = DNSProviderKind.Cloudflare, Hostname = "media.example.com", Zone = "media.example.com" }
        };
        Assert.Equal("exact", AcmePlan.For(_names, records, CloudflareAndDuck).Records["media.example.com"].Name);
    }

    [Theory]
    [InlineData("media.example.com", "_acme-challenge.media.example.com")]
    [InlineData("*.example.com", "_acme-challenge.example.com")]
    public void TxtName_UsesTheWildcardsBase(string name, string expected)
        => Assert.Equal(expected, AcmePlan.TxtName(name));

    [Fact]
    public void TxtAnswers_ReadsDnsOverHttpsJson()
    {
        const string Body = "{\"Status\":0,\"Answer\":[{\"name\":\"_acme-challenge.example.com\",\"type\":16,\"data\":\"\\\"abc\\\"\"},{\"data\":\"def\"}]}";
        Assert.Equal(new[] { "abc", "def" }, AcmeCertificateService.TxtAnswers(Body));
        Assert.Empty(AcmeCertificateService.TxtAnswers("{\"Status\":3}"));
        Assert.Empty(AcmeCertificateService.TxtAnswers("not json"));
    }

    private static bool CloudflareAndDuck(DNSProviderKind kind) => kind is DNSProviderKind.Cloudflare or DNSProviderKind.DuckDns;

    private (AcmeCertificateService Service, List<ActivityLog> Entries) Service()
    {
        var entries = new List<ActivityLog>();
        var manager = Substitute.For<IActivityManager>();
        manager.CreateAsync(Arg.Do<ActivityLog>(entries.Add));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
        var service = new AcmeCertificateService(
            new ProxyPaths(_dir),
            new Http01ChallengeStore(),
            http,
            NullLogger<AcmeCertificateService>.Instance,
            activity: new ActivityLogger(manager, Substitute.For<ILogger<ActivityLogger>>()));
        return (service, entries);
    }

    // Stands in for what Let's Encrypt would issue: a PEM chain and key in the issued-certificate files.
    private static void WriteIssuedFiles(ProxyPaths paths, string name)
    {
        Directory.CreateDirectory(paths.AcmeDirectory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        File.WriteAllText(paths.AcmeChainFile, cert.ExportCertificatePem());
        File.WriteAllText(paths.AcmeKeyFile, key.ExportPkcs8PrivateKeyPem());
    }
}
