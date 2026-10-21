using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// What the People side needs to know about the proxy's settings.
/// </summary>
internal static class ProxyCountryRules
{
    /// <summary>
    /// Whether country rules are on in the given proxy settings.
    /// </summary>
    /// <param name="settings">The proxy settings.</param>
    /// <returns>True when country rules need the location database.</returns>
    public static bool CountryRulesOn(ProxySettings? settings)
        => settings is not null && settings.Enabled && CountryRules.NeedsDatabase(ProxyOptions.From(settings.Advanced));
}
