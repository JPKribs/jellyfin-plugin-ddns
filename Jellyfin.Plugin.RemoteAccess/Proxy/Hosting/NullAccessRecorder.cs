using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// An access recorder that records nothing, for proxies started without one (probes and tests).
/// </summary>
public sealed class NullAccessRecorder : IAccessRecorder
{
    /// <summary>Gets the shared instance.</summary>
    public static IAccessRecorder Instance { get; } = new NullAccessRecorder();

    /// <inheritdoc />
    public void RecordRequest(IPAddress client, string url, string userAgent, bool local)
    {
    }

    /// <inheritdoc />
    public void RecordSignInFailure(IPAddress client, string url)
    {
    }
}
