namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The defaults the dashboard shows: the ones in effect, and both sets the server's memory chooses between.
/// </summary>
public sealed class ProxyDefaults
{
    /// <summary>Gets or sets the defaults this server runs with.</summary>
    public ProxyOptions Values { get; set; } = ProxyOptions.Standard;

    /// <summary>Gets or sets the defaults for a server with enough memory.</summary>
    public ProxyOptions Standard { get; set; } = ProxyOptions.Standard;

    /// <summary>Gets or sets the defaults for a server short on memory.</summary>
    public ProxyOptions LowMemory { get; set; } = ProxyOptions.LowMemory;

    /// <summary>Gets or sets a value indicating whether this server uses the low-memory defaults.</summary>
    public bool IsLowMemory { get; set; }

    /// <summary>Gets or sets the memory available to Jellyfin in MB, 0 while unknown.</summary>
    public long MemoryMb { get; set; }

    /// <summary>Gets or sets the memory below which the low-memory defaults apply, in MB.</summary>
    public long LowMemoryBelowMb { get; set; }
}
