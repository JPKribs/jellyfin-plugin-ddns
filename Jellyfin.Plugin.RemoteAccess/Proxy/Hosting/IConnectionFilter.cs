using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Refuses internet connections before TLS starts, for example a banned or blocked address. Local clients
/// are never filtered.
/// </summary>
public interface IConnectionFilter
{
    /// <summary>
    /// Whether an internet address may connect.
    /// </summary>
    /// <param name="address">The normalized remote address.</param>
    /// <param name="runtime">The current runtime, for the rules in force.</param>
    /// <param name="countries">Whether country rules apply; bans and the block list always do.</param>
    /// <param name="record">Whether a refusal is recorded; off when an already counted connection is checked again.</param>
    /// <returns>True to accept the connection.</returns>
    bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true);

    /// <summary>
    /// Counts an internet connection the connection limits refused, so it shows in the summary of refused
    /// connections. Filters that keep no summary ignore it.
    /// </summary>
    /// <param name="address">The normalized remote address.</param>
    void RecordLimited(IPAddress address)
    {
    }

    /// <summary>
    /// Readies the filter before the proxy starts listening, such as reading saved bans, so its rules apply
    /// from the first connection.
    /// </summary>
    void Prepare()
    {
    }
}
