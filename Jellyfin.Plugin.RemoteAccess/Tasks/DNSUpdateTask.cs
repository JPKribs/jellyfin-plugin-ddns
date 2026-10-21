using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// The recurring task that checks the public IP and updates records on change.
/// </summary>
public sealed class DNSUpdateTask : PluginScheduledTask
{
    private readonly DNSUpdateService _updateService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DNSUpdateTask"/> class.
    /// </summary>
    /// <param name="updateService">The update service.</param>
    public DNSUpdateTask(DNSUpdateService updateService)
    {
        _updateService = updateService;
    }

    /// <inheritdoc />
    public override string Name => "Update DDNS";

    /// <inheritdoc />
    public override string Key => "DynamicDNSUpdate";

    /// <inheritdoc />
    public override string Description => "Finds the server's public IP and updates each DDNS record that points somewhere else.";

    /// <inheritdoc />
    public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);
        await _updateService.RunAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Seeds the default schedule only. Administrators change these under Scheduled Tasks.
        // Run every 15 minutes and once on server startup.
        yield return EveryInterval(TimeSpan.FromMinutes(15));
        yield return OnStartup();
    }
}
