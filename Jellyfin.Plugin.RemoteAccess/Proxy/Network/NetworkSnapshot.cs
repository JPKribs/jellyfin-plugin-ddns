using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// The Jellyfin network settings trusting the proxy changes, captured before it changes them. Restoring
/// undoes only those changes, so anything an administrator changed in the meantime is kept.
/// </summary>
public sealed class NetworkSnapshot
{
    /// <summary>Gets or sets a value indicating whether remote access was on.</summary>
    public bool EnableRemoteAccess { get; set; }

    /// <summary>Gets or sets the bind addresses.</summary>
    public List<string> LocalNetworkAddresses { get; set; } = new();

    /// <summary>Gets or sets the known proxies.</summary>
    public List<string> KnownProxies { get; set; } = new();

    /// <summary>Gets or sets the published server URLs by subnet.</summary>
    public List<string> PublishedServerUriBySubnet { get; set; } = new();

    /// <summary>
    /// Gets or sets the public URL entry trusting the proxy wrote, so Undo removes only that one and keeps one an
    /// administrator put there since. Null in snapshots from before this was recorded.
    /// </summary>
    public string? WrittenExternal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether trusting the proxy bound Jellyfin to this machine only, replacing
    /// <see cref="LocalNetworkAddresses"/>, which Undo then puts back.
    /// </summary>
    public bool BoundToLoopback { get; set; }

    /// <summary>
    /// Gets or sets the URL entry older versions wrote for local clients while Jellyfin was bound to this machine
    /// only, so Undo still removes it. Empty when none was written.
    /// </summary>
    public string WrittenInternal { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL entries written for every client while Jellyfin is bound to this machine only, so Undo
    /// removes only those. Empty when none were written.
    /// </summary>
    public List<string> WrittenLocal { get; set; } = new();

    /// <summary>Gets every local entry an apply wrote, older versions' included.</summary>
    public IEnumerable<string> AllWrittenLocal
        => string.IsNullOrEmpty(WrittenInternal) ? WrittenLocal : WrittenLocal.Append(WrittenInternal);
}
