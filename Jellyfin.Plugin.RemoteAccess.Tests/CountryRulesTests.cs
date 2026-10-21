using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Country rules: which internet connections are allowed, and the safe behaviour around missing data.
/// </summary>
public class CountryRulesTests
{
    private static ProxyOptions Options(CountryRuleMode mode, bool blockUnknown = false, bool blockWithoutDb = false, params string[] countries)
        => ProxyOptions.From(new ProxyAdvancedSettings
        {
            CountryMode = mode,
            Countries = new List<string>(countries),
            BlockUnknownCountries = blockUnknown,
            BlockWithoutDatabase = blockWithoutDb
        });

    [Fact]
    public void Off_AllowsEverything()
    {
        var o = Options(CountryRuleMode.Off, true, true, "RU");
        Assert.True(CountryRules.Allows(o, true, "RU"));
        Assert.True(CountryRules.Allows(o, false, string.Empty));
        Assert.False(CountryRules.NeedsDatabase(o));
    }

    [Fact]
    public void Block_RefusesListedOnly()
    {
        var o = Options(CountryRuleMode.Block, countries: new[] { "RU", "CN" });
        Assert.False(CountryRules.Allows(o, true, "RU"));
        Assert.False(CountryRules.Allows(o, true, "cn"));
        Assert.True(CountryRules.Allows(o, true, "US"));
    }

    [Fact]
    public void AllowOnly_RefusesEverythingElse()
    {
        var o = Options(CountryRuleMode.AllowOnly, countries: new[] { "US", "CA" });
        Assert.True(CountryRules.Allows(o, true, "US"));
        Assert.False(CountryRules.Allows(o, true, "DE"));
    }

    [Fact]
    public void UnknownAddresses_AllowedUnlessSet()
    {
        Assert.True(CountryRules.Allows(Options(CountryRuleMode.AllowOnly, countries: "US"), true, string.Empty));
        Assert.False(CountryRules.Allows(Options(CountryRuleMode.AllowOnly, blockUnknown: true, countries: "US"), true, string.Empty));
    }

    [Fact]
    public void MissingDatabase_FailsOpenUnlessSet()
    {
        // Without the database nothing is known; the default lets everyone in rather than shutting everyone out.
        Assert.True(CountryRules.Allows(Options(CountryRuleMode.AllowOnly, countries: "US"), false, string.Empty));
        Assert.False(CountryRules.Allows(Options(CountryRuleMode.AllowOnly, blockWithoutDb: true, countries: "US"), false, string.Empty));
    }

    [Theory]
    [InlineData(LocationDetail.Off, false, LocationDetail.Off)]
    [InlineData(LocationDetail.Off, true, LocationDetail.Country)]
    [InlineData(LocationDetail.City, true, LocationDetail.City)]
    [InlineData(LocationDetail.Country, false, LocationDetail.Country)]
    public void RulesKeepTheDatabaseLoaded(LocationDetail people, bool rulesOn, LocationDetail expected)
        => Assert.Equal(expected, CountryRules.RequiredDetail(people, rulesOn));

    [Fact]
    public void Validator_NormalizesCodesAndNeedsACountryToAllow()
    {
        var s = new ProxySettings();
        s.Advanced.CountryMode = CountryRuleMode.AllowOnly;
        s.Advanced.Countries = new List<string> { " us", "US", "Canada", "x1", "ca" };
        ProxySettingsValidator.Normalize(s);
        Assert.Equal(new[] { "US", "CA" }, s.Advanced.Countries);
        Assert.Empty(ProxySettingsValidator.Validate(s));

        s.Advanced.Countries = new List<string>();
        Assert.Contains(ProxySettingsValidator.Validate(s), e => e.Contains("at least one country", System.StringComparison.Ordinal));
    }
}
