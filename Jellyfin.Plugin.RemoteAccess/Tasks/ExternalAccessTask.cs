using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Router;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// Runs the same outside check as step 6's Test from Outside on a schedule, so a router or provider change that
/// closes port 443 shows up in the activity log before people report it. The error is written once when the
/// failure starts and an entry when it clears, not on every run. A check that could not run at all, such as when
/// the outside service is busy, says nothing about the port and changes neither.
/// </summary>
public sealed class ExternalAccessTask : PluginScheduledTask
{
    private readonly ProxyManager _proxy;
    private readonly ActivityLogger _activity;
    private readonly ILogger<ExternalAccessTask> _logger;
    private bool _failing;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalAccessTask"/> class.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="logger">The logger.</param>
    public ExternalAccessTask(ProxyManager proxy, ActivityLogger activity, ILogger<ExternalAccessTask> logger)
    {
        _proxy = proxy;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string Name => "Verify External Access";

    /// <inheritdoc />
    public override string Key => "DynamicDNSVerifyExternalAccess";

    /// <inheritdoc />
    public override string Description => "Asks a few places on the internet to connect to your public address on port 443, and writes an error to the activity log when none can.";

    /// <inheritdoc />
    public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);

        var enabled = Plugin.Instance?.ReadConfiguration(c => c.Proxy?.Enabled == true) ?? false;
        if (!enabled)
        {
            progress.Report(100);
            return;
        }

        var result = await _proxy.CheckReachabilityAsync(cancellationToken).ConfigureAwait(false);
        if (result.Reachable is null)
        {
            _logger.LogInformation("External access could not be verified: {Error}", result.Error);
        }
        else if (result.Reachable == false)
        {
            var tried = string.Join("; ", result.Nodes.Select(n => (n.Location.Length > 0 ? n.Location : "Unknown") + ": " + n.Error));
            var message = tried.Length > 0 ? result.Error + " " + tried : result.Error;
            _logger.LogError("External access failed: {Message}", message);
            if (!_failing)
            {
                _failing = true;
                _activity.Log("Remote Access cannot be reached from outside", "DynamicDNS.ExternalAccessFailed", ActivityLogger.Trim(message), LogLevel.Error);
            }
        }
        else if (_failing)
        {
            _failing = false;
            _activity.Log("Remote Access can be reached from outside again", "DynamicDNS.ExternalAccessRestored", $"An outside location connected to port {ReachabilityChecker.Port}.");
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Seeds the default schedule only. Administrators change it under Scheduled Tasks.
        yield return EveryInterval(TimeSpan.FromHours(1));
    }
}
