using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Decides whether a country may connect. Pure, so every case is unit-tested.
/// </summary>
public static class CountryRules
{
    /// <summary>
    /// Whether an internet connection from a country may proceed.
    /// </summary>
    /// <param name="options">The resolved options holding the rule.</param>
    /// <param name="databaseLoaded">Whether the location database is available.</param>
    /// <param name="countryCode">The two-letter country of the address, empty when the database does not know it.</param>
    /// <param name="serverCountry">The two-letter country this server is in, empty while unknown.</param>
    /// <returns>True to allow.</returns>
    public static bool Allows(ProxyOptions options, bool databaseLoaded, string countryCode, string serverCountry = "")
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CountryMode == CountryRuleMode.Off)
        {
            return true;
        }

        if (!databaseLoaded)
        {
            return !options.BlockWithoutDatabase;
        }

        if (string.IsNullOrEmpty(countryCode))
        {
            return !options.BlockUnknownCountries;
        }

        var listed = options.Countries.Contains(countryCode, StringComparer.OrdinalIgnoreCase);

        // The server's own country plus any listed. Until the server's country is known there is nothing to
        // compare with, so nobody is cut off.
        if (options.CountryMode == CountryRuleMode.ServerCountry)
        {
            return string.IsNullOrEmpty(serverCountry) || listed || string.Equals(countryCode, serverCountry, StringComparison.OrdinalIgnoreCase);
        }
        return options.CountryMode == CountryRuleMode.Block ? !listed : listed;
    }

    /// <summary>
    /// Whether a rule needs the location database.
    /// </summary>
    /// <param name="options">The resolved options.</param>
    /// <returns>True when country rules are on.</returns>
    public static bool NeedsDatabase(ProxyOptions options) => options?.CountryMode is CountryRuleMode.ServerCountry or CountryRuleMode.Block or CountryRuleMode.AllowOnly;

    /// <summary>
    /// The location database detail to keep loaded: what the People page asks for, and at least country
    /// detail while country rules are on.
    /// </summary>
    /// <param name="people">The People page's detail.</param>
    /// <param name="rulesOn">Whether country rules are on.</param>
    /// <returns>The detail to load.</returns>
    public static LocationDetail RequiredDetail(LocationDetail people, bool rulesOn)
        => people == LocationDetail.Off && rulesOn ? LocationDetail.Country : people;
}
