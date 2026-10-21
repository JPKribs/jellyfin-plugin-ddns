namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Settings for the People page: who connected from where.
/// </summary>
public class PeopleSettings
{
    /// <summary>Gets or sets a value indicating whether sign-ins and connections are recorded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many days of history are kept.</summary>
    public int HistoryDays { get; set; } = 90;

    /// <summary>Gets or sets how much location is looked up.</summary>
    public LocationDetail Location { get; set; } = LocationDetail.Country;
}
