namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// How much location the People page shows for an internet address.
/// </summary>
public enum LocationDetail
{
    /// <summary>Country only, from a small database.</summary>
    Country,

    /// <summary>Country and city, from a larger database. Cities are often approximate.</summary>
    City,

    /// <summary>No location lookups and no database download.</summary>
    Off
}
