using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// The recurring task that checks the public IP and updates records on change.
/// </summary>
public sealed class DNSUpdateTask : PluginScheduledTask
{
    private readonly DNSUpdateService _updateService;
    private readonly ILogger<DNSUpdateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DNSUpdateTask"/> class.
    /// </summary>
    /// <param name="updateService">The update service.</param>
    /// <param name="logger">The logger.</param>
    public DNSUpdateTask(DNSUpdateService updateService, ILogger<DNSUpdateTask> logger)
    {
        _updateService = updateService;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string Name => "Update Dynamic DNS";

    /// <inheritdoc />
    public override string Key => "DynamicDNSUpdate";

    /// <inheritdoc />
    public override string Description => "Detects the server's public IP and updates configured DNS records when it changes.";

    /// <inheritdoc />
    public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report(0);

        var outcome = await _updateService.RunAsync(cancellationToken).ConfigureAwait(false);

        var records = outcome.Records;
        var total = records.Count;

        for (var i = 0; i < total; i++)
        {
            var record = records[i];

            if (!record.Success)
            {
                _logger.LogWarning("DDNS update for {Host}: {Message}", record.Hostname, record.Message);
            }

            progress.Report(total == 0 ? 100 : (double)(i + 1) / total * 100);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Seeds the default schedule only. Administrators change these under Scheduled Tasks.
        // Run every 15 minutes and once on server startup.
        yield return EveryInterval(TimeSpan.FromMinutes(15));
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
