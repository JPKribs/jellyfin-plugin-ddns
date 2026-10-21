using System;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// What the People page shows about the location database.
/// </summary>
public sealed class GeoIpStatus
{
    /// <summary>Gets or sets the configured detail.</summary>
    public LocationDetail Detail { get; set; }

    /// <summary>Gets or sets a value indicating whether a database is loaded.</summary>
    public bool Loaded { get; set; }

    /// <summary>Gets or sets when the loaded database was built (UTC).</summary>
    public DateTime? BuiltUtc { get; set; }

    /// <summary>Gets or sets the last download error, empty when none.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a download is running.</summary>
    public bool Downloading { get; set; }
}
