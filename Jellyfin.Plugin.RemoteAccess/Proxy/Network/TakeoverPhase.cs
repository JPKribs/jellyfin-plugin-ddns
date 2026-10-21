namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// Where the takeover of Jellyfin's ports stands.
/// </summary>
public enum TakeoverPhase
{
    /// <summary>Jellyfin's network settings are its own.</summary>
    Unmanaged,

    /// <summary>New settings are written and Jellyfin is restarting to pick them up.</summary>
    PendingRestart,

    /// <summary>Jellyfin runs behind the proxy and the last startup check passed.</summary>
    Managed,

    /// <summary>
    /// Written by older versions after an automatic rollback, which no longer happens. Kept so their state
    /// files still load; it is treated like <see cref="Unmanaged"/>.
    /// </summary>
    Degraded
}
