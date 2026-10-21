namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// What refused a connection.
/// </summary>
public enum BlockReason
{
    /// <summary>The address is on the block list.</summary>
    Address,

    /// <summary>The country rules refused it.</summary>
    Country,

    /// <summary>It is banned for too many failed sign-ins.</summary>
    Ban
}
