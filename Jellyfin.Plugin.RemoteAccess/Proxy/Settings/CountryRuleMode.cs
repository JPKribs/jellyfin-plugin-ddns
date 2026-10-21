namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// How the proxy treats internet connections by country.
/// </summary>
public enum CountryRuleMode
{
    /// <summary>No country rules.</summary>
    Off,

    /// <summary>Refuse connections from every country except the one this server is in and any listed.</summary>
    ServerCountry,

    /// <summary>Refuse connections from the listed countries.</summary>
    Block,

    /// <summary>Refuse connections from every country not listed.</summary>
    AllowOnly
}
