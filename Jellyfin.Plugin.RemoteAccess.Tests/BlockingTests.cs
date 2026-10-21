using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The block list and the server-country rule: what is refused, and what is buffered for the history.
/// </summary>
public sealed class BlockingTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-block-" + Guid.NewGuid().ToString("N"));
    private readonly BlockFilter _filter;
    private readonly BanList _bans;
    private readonly PeopleStore _store;

    public BlockingTests()
    {
        Directory.CreateDirectory(_dir);
        var http = Substitute.For<IHttpClientFactory>();
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_dir);
        var geo = new GeoIpService(new ProxyPaths(_dir), http, NullLogger<GeoIpService>.Instance);
        var server = new ServerLocation(
            new IPDetectionService(http, NullLogger<IPDetectionService>.Instance),
            new StatusStoreService(paths, NullLogger<StatusStoreService>.Instance),
            geo,
            NullLogger<ServerLocation>.Instance);
        _store = new PeopleStore(new ProxyPaths(_dir), NullLogger<PeopleStore>.Instance);
        _bans = new BanList(_store, geo, NullLogger<BanList>.Instance);
        _filter = new BlockFilter(geo, server, _bans, NullLogger<BlockFilter>.Instance);
    }

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
    public void BlockedAddress_IsRefusedAndBuffered()
    {
        var runtime = Runtime(CountryRuleMode.Off, "203.0.113.0/24", "198.51.100.9");
        Assert.False(_filter.Allow(IPAddress.Parse("203.0.113.40"), runtime));
        Assert.False(_filter.Allow(IPAddress.Parse("203.0.113.40"), runtime));
        Assert.False(_filter.Allow(IPAddress.Parse("198.51.100.9"), runtime));
        Assert.True(_filter.Allow(IPAddress.Parse("198.51.100.10"), runtime));

        var drained = _filter.Drain();
        Assert.Equal(2, drained.Count);
        var repeat = Assert.Single(drained, d => d.Ip == "203.0.113.40");
        Assert.Equal(2, repeat.Attempts);
        Assert.Equal(BlockReason.Address, repeat.Reason);
        Assert.Empty(_filter.Drain());
    }

    [Fact]
    public void Ban_AfterTooManyFailures_RefusesUntilLifted()
    {
        var runtime = Runtime(CountryRuleMode.Off);
        var options = runtime.Options with { BanFailures = 3 };
        var ip = IPAddress.Parse("203.0.113.66");

        _bans.RecordFailure(ip, options);
        _bans.RecordFailure(ip, options);
        Assert.True(_filter.Allow(ip, runtime));

        _bans.RecordFailure(ip, options);
        Assert.False(_filter.Allow(ip, runtime));
        var ban = Assert.Single(_bans.Active());
        Assert.Equal(3, ban.Failures);
        Assert.NotNull(ban.UntilUtc);
        Assert.Equal(BlockReason.Ban, Assert.Single(_filter.Drain()).Reason);

        // A ban is saved, so a restart keeps it.
        var reloaded = new BanList(_store, new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance), NullLogger<BanList>.Instance);
        Assert.True(reloaded.IsBanned(ip));

        Assert.True(_bans.Unban("203.0.113.66"));
        Assert.True(_filter.Allow(ip, runtime));
        Assert.False(_bans.Unban("203.0.113.66"));
        Assert.Empty(_store.GetBans());
    }

    [Fact]
    public void Ban_OffAtZero_AndForeverAtZeroMinutes()
    {
        var ip = IPAddress.Parse("203.0.113.67");
        var off = Runtime(CountryRuleMode.Off).Options with { BanFailures = 0 };
        for (var i = 0; i < 10; i++)
        {
            _bans.RecordFailure(ip, off);
        }

        Assert.False(_bans.IsBanned(ip));

        var forever = off with { BanFailures = 1, BanMinutes = 0 };
        _bans.RecordFailure(ip, forever);
        Assert.Null(Assert.Single(_bans.Active()).UntilUtc);
    }

    [Fact]
    public void Ban_ExpiredBanIsLiftedAndPruned()
    {
        var now = DateTime.UtcNow;
        _store.SaveBan(new Ban { Ip = "203.0.113.68", Failures = 5, BannedUtc = now.AddHours(-2), UntilUtc = now.AddHours(-1) });
        var bans = new BanList(_store, new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance), NullLogger<BanList>.Instance);
        Assert.False(bans.IsBanned(IPAddress.Parse("203.0.113.68")));
        Assert.Empty(bans.Active());

        _store.SaveBan(new Ban { Ip = "203.0.113.69", Failures = 5, BannedUtc = now.AddHours(-2), UntilUtc = now.AddHours(-1) });
        _store.Prune(90);
        Assert.Empty(_store.GetBans());
    }

    [Fact]
    public void Ban_IsWrittenToTheActivityLog()
    {
        var entries = new List<ActivityLog>();
        var manager = Substitute.For<IActivityManager>();
        manager.CreateAsync(Arg.Do<ActivityLog>(entries.Add));
        var activity = new ActivityLogger(manager, Substitute.For<ILogger<ActivityLogger>>());
        var geo = new GeoIpService(new ProxyPaths(_dir), Substitute.For<IHttpClientFactory>(), NullLogger<GeoIpService>.Instance);
        var bans = new BanList(_store, geo, NullLogger<BanList>.Instance, activity);
        var options = Runtime(CountryRuleMode.Off).Options with { BanFailures = 2, BanMinutes = 30 };

        bans.RecordFailure(IPAddress.Parse("203.0.113.70"), options);
        Assert.Empty(entries);
        bans.RecordFailure(IPAddress.Parse("203.0.113.70"), options);

        var entry = Assert.Single(entries);
        Assert.Equal("Remote Access banned 203.0.113.70", entry.Name);
        Assert.Equal("DynamicDNS.AddressBanned", entry.Type);
        Assert.Equal("2 failed sign-ins from an unknown country. Banned for 30 minutes.", entry.ShortOverview);
    }

    [Fact]
    public void Ban_CoversTheWholeIPv6Slash64()
    {
        var runtime = Runtime(CountryRuleMode.Off);
        var options = runtime.Options with { BanFailures = 3 };

        // Three guesses from three addresses in one /64 are one client.
        Assert.False(_bans.RecordFailure(IPAddress.Parse("2001:db8:1:2::a"), options));
        Assert.False(_bans.RecordFailure(IPAddress.Parse("2001:db8:1:2::b"), options));
        Assert.True(_bans.RecordFailure(IPAddress.Parse("2001:db8:1:2::c"), options));

        Assert.False(_filter.Allow(IPAddress.Parse("2001:db8:1:2:ffff::1"), runtime));
        Assert.True(_filter.Allow(IPAddress.Parse("2001:db8:1:3::1"), runtime));
        Assert.Equal("2001:db8:1:2::/64", Assert.Single(_bans.Active()).Ip);
        Assert.True(_bans.Unban("2001:db8:1:2::/64"));
    }

    [Fact]
    public void Ban_FullTrackerKeepsEveryonesCounts()
    {
        // Filling the tracker with fresh addresses must not reset the count of an address already guessing.
        var options = Runtime(CountryRuleMode.Off).Options with { BanFailures = 2 };
        var attacker = IPAddress.Parse("203.0.113.90");
        _bans.RecordFailure(attacker, options);
        for (var i = 0; i < 10_000; i++)
        {
            _bans.RecordFailure(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), options with { BanFailures = 100 });
        }

        Assert.True(_bans.RecordFailure(attacker, options));
    }

    [Fact]
    public void Validator_RefusesUnknownEnumValues()
    {
        var s = new ProxySettings();
        s.Advanced.CountryMode = (CountryRuleMode)9;
        s.Certificate.Mode = (CertificateMode)7;
        var errors = ProxySettingsValidator.Validate(s);
        Assert.Contains(errors, e => e.StartsWith("Country rule", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Certificate source", StringComparison.Ordinal));
    }

    [Fact]
    public void Ban_DefaultsAreOnAndAnHour()
    {
        var o = ProxyOptions.From(new ProxyAdvancedSettings());
        Assert.Equal((5, 10, 60), (o.BanFailures, o.BanWindowMinutes, o.BanMinutes));
    }

    [Fact]
    public void ServerCountry_WithoutDatabase_AllowsByDefault()
    {
        // The default rule must never shut everyone out while the database or the server's country is unknown.
        var runtime = Runtime(CountryRuleMode.ServerCountry);
        Assert.True(_filter.Allow(IPAddress.Parse("198.51.100.10"), runtime));
        Assert.Empty(_filter.Drain());
    }

    [Fact]
    public void ServerCountry_AllowsOnlyTheServersCountry()
    {
        var o = ProxyOptions.From(new ProxyAdvancedSettings { CountryMode = CountryRuleMode.ServerCountry });
        Assert.True(CountryRules.NeedsDatabase(o));
        Assert.True(CountryRules.Allows(o, true, "US", "US"));
        Assert.True(CountryRules.Allows(o, true, "us", "US"));
        Assert.False(CountryRules.Allows(o, true, "DE", "US"));
        Assert.True(CountryRules.Allows(o, true, "DE", string.Empty));
        Assert.True(CountryRules.Allows(o, true, string.Empty, "US"));

        // Listed countries are allowed alongside the server's own.
        var extra = ProxyOptions.From(new ProxyAdvancedSettings { CountryMode = CountryRuleMode.ServerCountry, Countries = new List<string> { "DE" } });
        Assert.True(CountryRules.Allows(extra, true, "DE", "US"));
        Assert.False(CountryRules.Allows(extra, true, "FR", "US"));
    }

    [Fact]
    public void ServerCountry_IsTheDefault()
        => Assert.Equal(CountryRuleMode.ServerCountry, ProxyOptions.From(new ProxyAdvancedSettings()).CountryMode);

    [Fact]
    public void Validator_ChecksBlockedAddresses()
    {
        var s = new ProxySettings();
        s.Advanced.BlockedAddresses = new List<string> { " 203.0.113.7 ", "203.0.113.7", "", "2001:db8::/32", "nope" };
        ProxySettingsValidator.Normalize(s);
        Assert.Equal(new[] { "203.0.113.7", "2001:db8::/32", "nope" }, s.Advanced.BlockedAddresses);
        Assert.Contains(ProxySettingsValidator.Validate(s), e => e.Contains("\"nope\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7/32", "203.0.113.7", true)]
    [InlineData("203.0.113.0/24", "203.0.113.7", false)]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("2001:db8::/64", "2001:db8::1", false)]
    [InlineData("203.0.113.8", "203.0.113.7", false)]
    public void BlockGuard_NamesOnlyOneAddress(string entry, string ip, bool expected)
        => Assert.Equal(expected, Jellyfin.Plugin.DynamicDns.Api.BlockGuard.NamesOnly(entry, IPAddress.Parse(ip)));

    [Fact]
    public void FullTracker_StillCountsANewAddress()
    {
        // An attacker keeps the tracker full with one failure from each of many addresses, then guesses from
        // one more. That address must still be counted and banned.
        var options = Runtime(CountryRuleMode.Off).Options with { BanFailures = 5, BanWindowMinutes = 10 };
        for (var i = 0; i < 20_000; i++)
        {
            _bans.RecordFailure(new IPAddress(new byte[] { 198, 18, (byte)(i >> 8), (byte)i }), options);
        }

        var guesser = IPAddress.Parse("203.0.113.200");
        var banned = false;
        for (var i = 0; i < 5; i++)
        {
            banned = _bans.RecordFailure(guesser, options);
        }

        Assert.True(banned);
        Assert.True(_bans.IsBanned(guesser));
    }

    private static ProxyRuntime Runtime(CountryRuleMode mode, params string[] blocked)
    {
        var settings = new ProxySettings { Enabled = true, Hostnames = new List<string> { "media.example.com" } };
        settings.Advanced.CountryMode = mode;
        settings.Advanced.BlockedAddresses = new List<string>(blocked);
        ProxySettingsValidator.Normalize(settings);
        var view = new JellyfinNetworkView(new Uri("http://127.0.0.1:8097/"), string.Empty, true, 8097, 0);
        return ProxyRuntimeFactory.Build(settings, view, _ => false, httpsAvailable: true);
    }
}
