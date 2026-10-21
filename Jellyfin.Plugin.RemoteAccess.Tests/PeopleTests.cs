using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The People history store, the proxy's access recorder, and location parsing.
/// </summary>
public sealed class PeopleTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-people-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PeopleStore _store;

    public PeopleTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new PeopleStore(new ProxyPaths(_dir), NullLogger<PeopleStore>.Instance);
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

    private static Observation Seen(string user, string ip, DateTime at, string country = "", string device = "phone", string host = "https://media.example.com:8920") => new()
    {
        UserId = user,
        UserName = user + "-name",
        Ip = ip,
        Host = host,
        DeviceId = device,
        DeviceName = "Phone",
        Client = "Jellyfin Android",
        CountryCode = country.Length > 0 ? country[..2].ToUpperInvariant() : string.Empty,
        Country = country,
        FirstSeenUtc = at,
        LastSeenUtc = at
    };

    [Fact]
    public void Observation_MergesAndKeepsTheLatest()
    {
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        _store.RecordObservation(Seen("u1", "203.0.113.5", first, "United States"));
        _store.RecordObservation(Seen("u1", "203.0.113.5", first.AddHours(3)));

        var row = Assert.Single(_store.GetActivity(10));
        Assert.Equal(ActivityType.User, row.Type);
        Assert.Equal("u1-name", row.User);
        Assert.Equal(first.AddHours(3), row.LastUtc);
        Assert.Equal("Phone, Jellyfin Android", row.Device);

        // A later sighting without a location keeps the one already known.
        Assert.Equal("United States", row.Country);

        // An older sighting arriving late does not move last seen backwards.
        _store.RecordObservation(Seen("u1", "203.0.113.5", first.AddHours(-5)));
        Assert.Equal(first.AddHours(3), _store.GetActivity(10).Single().LastUtc);
    }

    [Fact]
    public void Activity_OneRowPerUserAddressAndDevice_NewestFirst()
    {
        var now = DateTime.UtcNow;
        _store.RecordObservation(Seen("u1", "203.0.113.5", now.AddHours(-2), "United States"));
        _store.RecordObservation(Seen("u1", "198.51.100.9", now.AddHours(-1), "Canada", device: "tv"));
        _store.RecordObservation(Seen("u2", "192.168.1.20", now.AddMinutes(-5)));

        Assert.Equal(new[] { "192.168.1.20", "198.51.100.9", "203.0.113.5" }, _store.GetActivity(10).Select(r => r.Ip));
        Assert.Equal(2, _store.GetActivity(2).Count);
    }

    [Fact]
    public void Activity_GroupsFailuresByAddressAndUsername()
    {
        var now = DateTime.UtcNow;
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now.AddMinutes(-3), Username = "admin", Success = false, Ip = "203.0.113.7" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now.AddMinutes(-2), Username = "admin", Success = false, Ip = "203.0.113.7" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now.AddMinutes(-2), Username = "alice", UserId = "u1", Success = true, Ip = "203.0.113.5" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now.AddMinutes(-1), Success = false, Ip = "203.0.113.8" });

        var failures = _store.GetActivity(10);
        Assert.All(failures, r => Assert.Equal(ActivityType.Failure, r.Type));
        Assert.Equal(new[] { "203.0.113.8", "203.0.113.7" }, failures.Select(r => r.Ip));
        Assert.Equal(2, failures[1].Count);
        Assert.Equal("admin", failures[1].User);
    }

    [Fact]
    public void Activity_SeenOnlyWhenTheAddressDidNothingElse()
    {
        var now = DateTime.UtcNow;
        _store.RecordSeen(new[]
        {
            new SeenVisit { Ip = "192.0.2.1", Host = "https://media.example.com", Device = "Firefox, Windows", CountryCode = "DE", Country = "Germany", Requests = 4, FirstUtc = now, LastUtc = now },
            new SeenVisit { Ip = "203.0.113.5", Host = "https://media.example.com", Requests = 9, FirstUtc = now, LastUtc = now },
            new SeenVisit { Ip = "203.0.113.7", Host = "https://media.example.com", Requests = 2, FirstUtc = now, LastUtc = now },
            new SeenVisit { Ip = "203.0.113.9", Host = "https://media.example.com", Requests = 1, FirstUtc = now, LastUtc = now }
        });
        _store.RecordSeen(new[] { new SeenVisit { Ip = "192.0.2.1", Host = "https://media.example.com:8920", Requests = 1, FirstUtc = now, LastUtc = now } });
        _store.RecordObservation(Seen("u1", "203.0.113.5", now));
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Username = "admin", Success = false, Ip = "203.0.113.7" });
        _store.RecordBlocked(new[] { new BlockedAttempt { Ip = "203.0.113.9", Reason = BlockReason.Address, Attempts = 1, FirstUtc = now, LastUtc = now } });

        var rows = _store.GetActivity(10);
        var seen = Assert.Single(rows, r => r.Type == ActivityType.Seen);
        Assert.Equal("192.0.2.1", seen.Ip);
        Assert.Equal(5, seen.Count);
        Assert.Equal("Firefox, Windows", seen.Device);
        Assert.Equal("Germany", seen.Country);
        Assert.Equal("https://media.example.com:8920", seen.Host);
        Assert.Equal(BlockReason.Address, Assert.Single(rows, r => r.Type == ActivityType.Blocked).Reason);
    }

    [Fact]
    public void Activity_FiltersByTypeAndPlaceBeforeTheLimit()
    {
        var now = DateTime.UtcNow;
        var local = Seen("u2", "192.168.1.20", now.AddMinutes(-1));
        local.IsLocal = true;
        _store.RecordObservation(local);
        var abroad = Seen("u1", "203.0.113.5", now.AddHours(-2), "Canada");
        abroad.City = "Toronto";
        _store.RecordObservation(abroad);

        // A flood of failures from one country never pushes the one person out of a filtered view.
        for (var i = 0; i < 20; i++)
        {
            _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Username = "admin" + i, Success = false, Ip = "198.51.100." + i, CountryCode = "DE", Country = "Germany" });
        }

        var users = _store.GetActivity(5, ActivityType.User);
        Assert.Equal(new[] { "192.168.1.20", "203.0.113.5" }, users.Select(r => r.Ip));
        Assert.Equal("Toronto", users[1].City);

        Assert.Equal("203.0.113.5", Assert.Single(_store.GetActivity(5, place: "ca")).Ip);
        Assert.Equal("192.168.1.20", Assert.Single(_store.GetActivity(5, place: PeopleStore.LocalPlace)).Ip);
        Assert.Equal(5, _store.GetActivity(5, ActivityType.Failure, "DE").Count);
        Assert.Empty(_store.GetActivity(5, ActivityType.Failure, "CA"));
    }

    [Fact]
    public void ClearHistory_RemovesSetAsideCopies()
    {
        _store.RecordAttempt(new SignInAttempt { TimeUtc = DateTime.UtcNow, Username = "admin", Success = false, Ip = "203.0.113.7" });
        var folder = Path.GetDirectoryName(Directory.GetFiles(_dir, "people.db", SearchOption.AllDirectories)[0])!;
        var copy = Path.Join(folder, "people.db.corrupt-20260101-000000");
        File.WriteAllText(copy, "old history");

        _store.DeleteAll();

        Assert.Empty(_store.GetActivity(10));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(Path.Join(folder, "people.db")));
    }

    [Fact]
    public void Recorder_ClearBuffered_ForgetsVisitsAndFailures()
    {
        var recorder = new AccessRecorder();
        recorder.RecordRequest(IPAddress.Parse("203.0.113.5"), "https://x", "curl", local: false);
        recorder.RecordSignInFailure(IPAddress.Parse("203.0.113.5"), "https://x");
        recorder.ClearBuffered();
        Assert.Empty(recorder.DrainSeen());
        Assert.Empty(recorder.TakeUnclaimed(TimeSpan.Zero));
    }

    [Fact]
    public void Blocked_MergesByAddressAndCountsByCountry()
    {
        var now = DateTime.UtcNow;
        _store.RecordBlocked(new[]
        {
            new BlockedAttempt { Ip = "203.0.113.7", CountryCode = "RU", Country = "Russia", Reason = BlockReason.Country, Attempts = 3, FirstUtc = now.AddMinutes(-5), LastUtc = now.AddMinutes(-4) },
            new BlockedAttempt { Ip = "203.0.113.8", CountryCode = "RU", Country = "Russia", Reason = BlockReason.Address, Attempts = 1, FirstUtc = now, LastUtc = now },
            new BlockedAttempt { Ip = "198.51.100.1", Reason = BlockReason.Address, Attempts = 1, FirstUtc = now, LastUtc = now }
        });
        _store.RecordBlocked(new[] { new BlockedAttempt { Ip = "203.0.113.7", Reason = BlockReason.Country, Attempts = 2, FirstUtc = now, LastUtc = now } });

        // Two addresses from Russia; the address with no known country is kept but not on the map.
        var ru = Assert.Single(_store.GetBlockedByCountry());
        Assert.Equal("RU", ru.CountryCode);
        Assert.Equal(2, ru.Count);

        _store.DeleteAll();
        Assert.Empty(_store.GetBlockedByCountry());
    }

    [Fact]
    public void Prune_AndDeleteAll()
    {
        var now = DateTime.UtcNow;
        _store.RecordObservation(Seen("old", "203.0.113.1", now.AddDays(-100)));
        _store.RecordObservation(Seen("new", "203.0.113.2", now));
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now.AddDays(-100), Ip = "203.0.113.3" });
        _store.RecordSeen(new[] { new SeenVisit { Ip = "203.0.113.4", Requests = 1, FirstUtc = now.AddDays(-100), LastUtc = now.AddDays(-100) } });
        _store.Prune(90);
        Assert.Equal(new[] { "203.0.113.2" }, _store.GetActivity(10).Select(r => r.Ip));

        _store.DeleteAll();
        Assert.Empty(_store.GetActivity(10));
    }

    [Fact]
    public void CountryTotals_CountPeopleAndFailuresFromTheInternetOnly()
    {
        var now = DateTime.UtcNow;
        _store.RecordObservation(Seen("u1", "203.0.113.5", now, "United States"));
        _store.RecordObservation(Seen("u1", "203.0.113.6", now, "United States", device: "tv"));
        _store.RecordObservation(Seen("u2", "203.0.113.7", now, "United States"));
        _store.RecordObservation(Seen("u3", "198.51.100.9", now, "Canada"));
        var local = Seen("u4", "192.168.1.20", now, "United States");
        local.IsLocal = true;
        _store.RecordObservation(local);

        var people = _store.GetPeopleByCountry();
        Assert.Equal(new[] { ("UN", 2), ("CA", 1) }, people.Select(t => (t.CountryCode, t.Count)));

        _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Success = false, Ip = "203.0.113.9", CountryCode = "NL", Country = "Netherlands" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Success = false, Ip = "203.0.113.9", CountryCode = "NL", Country = "Netherlands" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Success = true, Ip = "203.0.113.5", CountryCode = "US", Country = "United States" });
        _store.RecordAttempt(new SignInAttempt { TimeUtc = now, Success = false, Ip = "192.168.1.20", IsLocal = true, CountryCode = "US", Country = "United States" });

        var failed = _store.GetFailedByCountry();
        Assert.Equal(new[] { ("NL", 2) }, failed.Select(t => (t.CountryCode, t.Count)));
    }

    [Fact]
    public void AccessRecorder_RemembersUrlAndPairsFailures()
    {
        var recorder = new AccessRecorder();
        var ip = IPAddress.Parse("203.0.113.5");
        recorder.RecordRequest(ip, "https://media.example.com:8920", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/131.0", local: false);
        Assert.Equal("https://media.example.com:8920", recorder.UrlFor("203.0.113.5"));
        Assert.Equal(string.Empty, recorder.UrlFor("203.0.113.6"));

        // Jellyfin reports a wrong password before the proxy sees the response, and a username that does not
        // exist not at all. Either order pairs; only the unpaired failure is left as an unknown username.
        Assert.Equal("https://media.example.com:8920", recorder.Claim("203.0.113.5"));
        recorder.RecordSignInFailure(ip, "https://media.example.com:8920");
        recorder.RecordSignInFailure(IPAddress.Parse("203.0.113.9"), "https://media.example.com:8920");
        recorder.RecordSignInFailure(IPAddress.Parse("203.0.113.10"), "https://media.example.com:8920");
        recorder.Claim("203.0.113.10");

        var unclaimed = recorder.TakeUnclaimed(TimeSpan.Zero);
        Assert.Equal(new[] { "203.0.113.9" }, unclaimed.Select(f => f.Ip));
        Assert.Empty(recorder.TakeUnclaimed(TimeSpan.Zero));
    }

    [Fact]
    public void AccessRecorder_BuffersInternetVisitsOnly()
    {
        var recorder = new AccessRecorder();
        recorder.RecordRequest(IPAddress.Parse("203.0.113.5"), "https://media.example.com", "Mozilla/5.0 (Windows NT 10.0) Firefox/131.0", local: false);
        recorder.RecordRequest(IPAddress.Parse("203.0.113.5"), "https://media.example.com:8920", "Mozilla/5.0 (Windows NT 10.0) Firefox/131.0", local: false);
        recorder.RecordRequest(IPAddress.Parse("192.168.1.20"), "http://192.168.1.10:8096", "Mozilla/5.0", local: true);

        var visit = Assert.Single(recorder.DrainSeen());
        Assert.Equal("203.0.113.5", visit.Ip);
        Assert.Equal(2, visit.Requests);
        Assert.Equal("Firefox, Windows", visit.Device);
        Assert.Equal("https://media.example.com:8920", visit.Host);
        Assert.Empty(recorder.DrainSeen());

        // The address used is still remembered for local clients.
        Assert.Equal("http://192.168.1.10:8096", recorder.UrlFor("192.168.1.20"));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", "Edge, Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari, iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", "Chrome, Android")]
    [InlineData("curl/8.7.1", "curl/8.7.1")]
    [InlineData("python-requests/2.32 (extra)", "python-requests/2.32")]
    [InlineData("", "")]
    public void UserAgents_DescribesTheCommonCases(string userAgent, string expected)
        => Assert.Equal(expected, UserAgents.Describe(userAgent));

    [Fact]
    public void AccessRecorder_WaitsOutTheGracePeriod()
    {
        var recorder = new AccessRecorder();
        recorder.RecordSignInFailure(IPAddress.Parse("203.0.113.9"), "https://x");
        Assert.Empty(recorder.TakeUnclaimed(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("203.0.113.5:51234", "203.0.113.5")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("", null)]
    [InlineData("not an address", null)]
    public void ParseAddress_HandlesWhatJellyfinReports(string value, string? expected)
        => Assert.Equal(expected, PeopleService.ParseAddress(value)?.ToString());

    [Fact]
    public void Geo_ParsesCountryAndCity()
    {
        var record = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "NL", ["names"] = new Dictionary<string, object> { ["en"] = "Netherlands" } },
            ["city"] = new Dictionary<string, object> { ["names"] = new Dictionary<string, object> { ["en"] = "Amsterdam" } }
        };
        Assert.Equal(new GeoLocation("NL", "Netherlands", "Amsterdam"), GeoIpService.Parse(record));
        Assert.Equal(GeoLocation.Unknown, GeoIpService.Parse(null));
        Assert.Equal(new GeoLocation(string.Empty, string.Empty, string.Empty), GeoIpService.Parse(new Dictionary<string, object>()));
    }

    [Fact]
    public void Geo_DownloadUrlPerEditionAndMonth()
    {
        var month = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("https://download.db-ip.com/free/dbip-country-lite-2026-03.mmdb.gz", GeoIpService.DownloadUrl(LocationDetail.Country, month).ToString());
        Assert.Equal("https://download.db-ip.com/free/dbip-city-lite-2026-03.mmdb.gz", GeoIpService.DownloadUrl(LocationDetail.City, month).ToString());
    }
}
