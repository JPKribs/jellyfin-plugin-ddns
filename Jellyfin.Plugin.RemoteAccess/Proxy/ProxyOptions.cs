using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The values the proxy runs with: every advanced setting the administrator left empty, filled in from the
/// built-in defaults. Resolved in one place so no other code needs to know a default.
/// </summary>
public sealed record ProxyOptions
{
    /// <summary>Servers with less memory than this, in bytes, use <see cref="LowMemory"/>.</summary>
    public const long LowMemoryBelowBytes = 3L * 1024 * 1024 * 1024;

    private static long _memoryBytes;

    /// <summary>Gets the defaults for a server with enough memory.</summary>
    public static ProxyOptions Standard { get; } = new();

    /// <summary>
    /// Gets the defaults for a server short on memory, such as a 1 or 2 GB single-board computer: fewer
    /// connections held open, and idle ones closed sooner. Uploads are streamed, so their limit is unchanged.
    /// </summary>
    public static ProxyOptions LowMemory { get; } = Standard with { MaxConnections = 500, MaxConnectionsPerIp = 32, KeepAliveSeconds = 60 };

    /// <summary>
    /// Gets the memory available to Jellyfin in bytes, which honours a container's limit; 0 while unknown.
    /// </summary>
    public static long MemoryBytes
    {
        get
        {
            if (_memoryBytes == 0)
            {
                // Known once the runtime has collected at least once, which it has long before anyone asks.
                _memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }

            return _memoryBytes;
        }
    }

    /// <summary>Gets a value indicating whether this server uses the low-memory defaults.</summary>
    public static bool IsLowMemory => MemoryBytes > 0 && MemoryBytes < LowMemoryBelowBytes;

    /// <summary>Gets the defaults this server runs with.</summary>
    public static ProxyOptions Defaults => IsLowMemory ? LowMemory : Standard;

    /// <summary>The HTTP port seen from the internet: always 80, forwarded on the router.</summary>
    public const int PublicHttpPort = 80;

    /// <summary>The HTTPS port seen from the internet: always 443, forwarded on the router.</summary>
    public const int PublicHttpsPort = 443;

    /// <summary>Gets the plain HTTP port. Jellyfin keeps its own ports; the proxy uses the standard ones.</summary>
    public int HttpPort { get; init; } = PublicHttpPort;

    /// <summary>Gets the HTTPS port.</summary>
    public int HttpsPort { get; init; } = PublicHttpsPort;

    /// <summary>Gets the LAN address to advertise, empty to detect it.</summary>
    public string LanAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the proxy asks the router to forward its ports with UPnP or NAT-PMP. On by
    /// default, as in Plex: most home routers allow it, and the forwards go away when the proxy stops.
    /// </summary>
    public bool AutoPortForwarding { get; init; } = true;

    /// <summary>Gets a value indicating whether HSTS is sent.</summary>
    public bool HstsEnabled { get; init; } = true;

    /// <summary>Gets the HSTS max-age in days.</summary>
    public int HstsMaxAgeDays { get; init; } = 180;

    /// <summary>Gets a value indicating whether pages may only be framed by the same origin.</summary>
    public bool SameOriginFramesOnly { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether Jellyfin listens on this machine only while it trusts the proxy. Off by
    /// default: it changes how local apps, casting and discovery reach Jellyfin.
    /// </summary>
    public bool JellyfinLoopbackOnly { get; init; }

    /// <summary>Gets extra networks treated like the LAN.</summary>
    public IReadOnlyList<string> TrustedNetworks { get; init; } = Array.Empty<string>();

    /// <summary>Gets internet addresses that are always refused, as addresses or CIDR blocks.</summary>
    public IReadOnlyList<string> BlockedAddresses { get; init; } = Array.Empty<string>();

    /// <summary>Gets how many failed sign-ins within the window ban an internet address; 0 turns bans off.</summary>
    public int BanFailures { get; init; } = 5;

    /// <summary>Gets the window failed sign-ins are counted over, in minutes.</summary>
    public int BanWindowMinutes { get; init; } = 10;

    /// <summary>Gets how long a ban lasts, in minutes; 0 lasts until it is lifted.</summary>
    public int BanMinutes { get; init; } = 60;

    /// <summary>Gets how internet connections are treated by country.</summary>
    public CountryRuleMode CountryMode { get; init; } = CountryRuleMode.ServerCountry;

    /// <summary>Gets the two-letter codes the country rule lists.</summary>
    public IReadOnlyList<string> Countries { get; init; } = Array.Empty<string>();

    /// <summary>Gets a value indicating whether addresses the location database does not know are refused.</summary>
    public bool BlockUnknownCountries { get; init; }

    /// <summary>Gets a value indicating whether internet connections are refused while the location database is unavailable.</summary>
    public bool BlockWithoutDatabase { get; init; }

    /// <summary>Gets a value indicating whether HTTP/2 is offered.</summary>
    public bool EnableHttp2 { get; init; } = true;

    /// <summary>Gets the keep-alive timeout in seconds.</summary>
    public int KeepAliveSeconds { get; init; } = 130;

    /// <summary>Gets the request headers timeout in seconds.</summary>
    public int RequestHeadersTimeoutSeconds { get; init; } = 15;

    /// <summary>Gets the request header size limit in KB.</summary>
    public int MaxRequestHeadersKb { get; init; } = 32;

    /// <summary>Gets the global connection limit, zero for none.</summary>
    public int MaxConnections { get; init; } = 2000;

    /// <summary>Gets the per internet address connection limit, zero for none.</summary>
    public int MaxConnectionsPerIp { get; init; } = 64;

    /// <summary>Gets the internet upload limit in MB, zero for none.</summary>
    public int MaxRequestBodyMbWan { get; init; } = 100;

    /// <summary>Gets the LAN upload limit in MB, zero for none.</summary>
    public int MaxRequestBodyMbLan { get; init; }

    /// <summary>Gets the minimum read rate for non-media responses, zero for off.</summary>
    public int MinResponseBytesPerSecond { get; init; } = 240;

    /// <summary>Gets the backend silence timeout in seconds.</summary>
    public int BackendActivityTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Resolves stored advanced settings against the defaults.
    /// </summary>
    /// <param name="advanced">The stored settings, or null for all defaults.</param>
    /// <returns>The resolved options.</returns>
    public static ProxyOptions From(ProxyAdvancedSettings? advanced) => From(advanced, Defaults);

    /// <summary>
    /// Resolves stored advanced settings against a given set of defaults.
    /// </summary>
    /// <param name="advanced">The stored settings, or null for all defaults.</param>
    /// <param name="defaults">The defaults to fill empty settings from.</param>
    /// <returns>The resolved options.</returns>
    public static ProxyOptions From(ProxyAdvancedSettings? advanced, ProxyOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var a = advanced ?? new ProxyAdvancedSettings();
        var d = defaults;
        return new ProxyOptions
        {
            HttpPort = a.HttpPort ?? d.HttpPort,
            HttpsPort = a.HttpsPort ?? d.HttpsPort,
            LanAddress = (a.LanAddress ?? string.Empty).Trim(),
            AutoPortForwarding = a.AutoPortForwarding ?? d.AutoPortForwarding,
            HstsEnabled = a.HstsEnabled ?? d.HstsEnabled,
            HstsMaxAgeDays = a.HstsMaxAgeDays ?? d.HstsMaxAgeDays,
            SameOriginFramesOnly = a.SameOriginFramesOnly ?? d.SameOriginFramesOnly,
            JellyfinLoopbackOnly = a.JellyfinLoopbackOnly ?? d.JellyfinLoopbackOnly,
            TrustedNetworks = (a.TrustedNetworks ?? new List<string>()).ToList(),
            BlockedAddresses = (a.BlockedAddresses ?? new List<string>()).ToList(),
            BanFailures = a.BanFailures ?? d.BanFailures,
            BanWindowMinutes = a.BanWindowMinutes ?? d.BanWindowMinutes,
            BanMinutes = a.BanMinutes ?? d.BanMinutes,
            CountryMode = a.CountryMode ?? d.CountryMode,
            Countries = (a.Countries ?? new List<string>()).ToList(),
            BlockUnknownCountries = a.BlockUnknownCountries ?? d.BlockUnknownCountries,
            BlockWithoutDatabase = a.BlockWithoutDatabase ?? d.BlockWithoutDatabase,
            EnableHttp2 = a.EnableHttp2 ?? d.EnableHttp2,
            KeepAliveSeconds = a.KeepAliveSeconds ?? d.KeepAliveSeconds,
            RequestHeadersTimeoutSeconds = a.RequestHeadersTimeoutSeconds ?? d.RequestHeadersTimeoutSeconds,
            MaxRequestHeadersKb = a.MaxRequestHeadersKb ?? d.MaxRequestHeadersKb,
            MaxConnections = a.MaxConnections ?? d.MaxConnections,
            MaxConnectionsPerIp = a.MaxConnectionsPerIp ?? d.MaxConnectionsPerIp,
            MaxRequestBodyMbWan = a.MaxRequestBodyMbWan ?? d.MaxRequestBodyMbWan,
            MaxRequestBodyMbLan = a.MaxRequestBodyMbLan ?? d.MaxRequestBodyMbLan,
            MinResponseBytesPerSecond = a.MinResponseBytesPerSecond ?? d.MinResponseBytesPerSecond,
            BackendActivityTimeoutSeconds = a.BackendActivityTimeoutSeconds ?? d.BackendActivityTimeoutSeconds
        };
    }
}
