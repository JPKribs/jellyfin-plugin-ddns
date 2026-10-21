using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.People;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// Downloads the newest location database for the Dashboard's map and the country rules. DB-IP publishes a new one each month.
/// </summary>
public sealed class LocationDatabaseTask : PluginScheduledTask
{
    private readonly GeoIpService _geo;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocationDatabaseTask"/> class.
    /// </summary>
    /// <param name="geo">The location lookup.</param>
    public LocationDatabaseTask(GeoIpService geo)
    {
        _geo = geo;
    }

    /// <inheritdoc />
    public override string Name => "Refresh Locations";

    /// <inheritdoc />
    public override string Key => "DynamicDNSLocationDatabase";

    /// <inheritdoc />
    public override string Description => "Downloads the newest DB-IP location database, used for the Dashboard's map and the country rules.";

    /// <inheritdoc />
    public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);

        // Nothing needs locations while history and country rules are off, so nothing is downloaded.
        var detail = PeopleService.RequiredLocation;
        if (detail != LocationDetail.Off)
        {
            await _geo.UpdateAsync(detail, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly is plenty for a monthly database; a missed month is retried the next week.
        yield return EveryInterval(TimeSpan.FromDays(7));
    }
}
