using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Advanced proxy values, stored only when the administrator changes them. A null value means "use the
/// default"; see <see cref="ProxyOptions"/> for the defaults and the resolved values the proxy runs with.
/// </summary>
public class ProxyAdvancedSettings
{
    /// <summary>Gets or sets the port the proxy serves plain HTTP on; the router forwards port 80 to it.</summary>
    public int? HttpPort { get; set; }

    /// <summary>Gets or sets the port the proxy serves HTTPS on; the router forwards port 443 to it.</summary>
    public int? HttpsPort { get; set; }

    /// <summary>Gets or sets the LAN address advertised to local clients. Empty detects it.</summary>
    public string LanAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the public user list is hidden from internet clients.</summary>
    public bool? HideUserListFromWan { get; set; }

    /// <summary>Gets or sets a value indicating whether HTTPS responses for configured hostnames carry HSTS.</summary>
    public bool? HstsEnabled { get; set; }

    /// <summary>Gets or sets the HSTS max-age in days.</summary>
    public int? HstsMaxAgeDays { get; set; }

    /// <summary>Gets or sets a value indicating whether pages may only be framed by the same origin.</summary>
    public bool? SameOriginFramesOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin listens on this machine only while it trusts the proxy, so
    /// every client, local ones too, reaches it through the proxy.
    /// </summary>
    public bool? JellyfinLoopbackOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether requests for the public names get the internet's path rules even
    /// from a local address, for setups where something in front of the proxy makes every visitor look local.
    /// </summary>
    public bool? PublicNamesAsInternet { get; set; }

    /// <summary>Gets or sets extra networks treated like the LAN, as addresses or CIDR blocks.</summary>
    public List<string> TrustedNetworks { get; set; } = new();

    /// <summary>Gets or sets internet addresses that are always refused, as addresses or CIDR blocks.</summary>
    public List<string> BlockedAddresses { get; set; } = new();

    /// <summary>Gets or sets how many failed sign-ins within the window ban an internet address; 0 turns bans off.</summary>
    public int? BanFailures { get; set; }

    /// <summary>Gets or sets the window failed sign-ins are counted over, in minutes.</summary>
    public int? BanWindowMinutes { get; set; }

    /// <summary>Gets or sets how long a ban lasts, in minutes; 0 lasts until it is lifted.</summary>
    public int? BanMinutes { get; set; }

    /// <summary>Gets or sets how internet connections are treated by country.</summary>
    public CountryRuleMode? CountryMode { get; set; }

    /// <summary>Gets or sets the two-letter codes of the countries the rule lists.</summary>
    public List<string> Countries { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether addresses the location database does not know are refused.</summary>
    public bool? BlockUnknownCountries { get; set; }

    /// <summary>Gets or sets a value indicating whether internet connections are refused while the location database is unavailable.</summary>
    public bool? BlockWithoutDatabase { get; set; }

    /// <summary>Gets or sets a value indicating whether HTTP/2 is offered on the HTTPS listener.</summary>
    public bool? EnableHttp2 { get; set; }

    /// <summary>Gets or sets the keep-alive timeout in seconds.</summary>
    public int? KeepAliveSeconds { get; set; }

    /// <summary>Gets or sets how long a client may take to send request headers, in seconds.</summary>
    public int? RequestHeadersTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the maximum total size of request headers, in KB.</summary>
    public int? MaxRequestHeadersKb { get; set; }

    /// <summary>Gets or sets the maximum number of open connections. Zero is unlimited.</summary>
    public int? MaxConnections { get; set; }

    /// <summary>Gets or sets the maximum number of open connections from one internet address. Zero is unlimited.</summary>
    public int? MaxConnectionsPerIp { get; set; }

    /// <summary>Gets or sets the largest request body accepted from the internet, in MB. Zero is unlimited.</summary>
    public int? MaxRequestBodyMbWan { get; set; }

    /// <summary>Gets or sets the largest request body accepted from the LAN, in MB. Zero is unlimited.</summary>
    public int? MaxRequestBodyMbLan { get; set; }

    /// <summary>Gets or sets the minimum rate a client must read non-media responses at, in bytes per second. Zero turns it off.</summary>
    public int? MinResponseBytesPerSecond { get; set; }

    /// <summary>Gets or sets how long the backend may go silent on a request or WebSocket, in seconds.</summary>
    public int? BackendActivityTimeoutSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the proxy compresses Jellyfin's text responses.</summary>
    public bool? CompressJellyfin { get; set; }
}
