using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Where the proxy reports failed sign-ins from the internet, so repeated failures can ban the address.
/// </summary>
public interface IFailureTracker
{
    /// <summary>
    /// Notes a failed sign-in from an internet address.
    /// </summary>
    /// <param name="client">The normalized client address.</param>
    /// <param name="options">The options in force, holding the ban limits.</param>
    /// <returns>
    /// True when this failure newly banned the client, so its connections should be closed. An address already
    /// banned gives false: its connections were closed when the ban landed.
    /// </returns>
    bool RecordFailure(IPAddress client, ProxyOptions options);
}
