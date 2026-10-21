using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// A failure tracker that bans nothing, for proxies started without one (probes and tests).
/// </summary>
public sealed class NullFailureTracker : IFailureTracker
{
    /// <summary>Gets the shared instance.</summary>
    public static IFailureTracker Instance { get; } = new NullFailureTracker();

    /// <inheritdoc />
    public bool RecordFailure(IPAddress client, ProxyOptions options) => false;
}
