using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// Keeps the proxy's certificate current: requests a Let's Encrypt one when it is missing, no longer covers the
/// addresses, or is within 30 days of expiry, reloads certificate files, replaces a self-signed one before it
/// expires, and warns in the activity log when a certificate is about to run out or cannot be loaded. The proxy
/// also notices file changes on its own; this is the scheduled safety net. Forcing a new certificate is the
/// Configuration tab's button, not a task.
/// </summary>
public sealed class CertificateRefreshTask : PluginScheduledTask
{
    private static readonly TimeSpan _warnBefore = TimeSpan.FromDays(14);

    private readonly ProxyManager _proxy;
    private readonly ActivityLogger _activity;
    private readonly ILogger<CertificateRefreshTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateRefreshTask"/> class.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="logger">The logger.</param>
    public CertificateRefreshTask(ProxyManager proxy, ActivityLogger activity, ILogger<CertificateRefreshTask> logger)
    {
        _proxy = proxy;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string Name => "Refresh Certificate";

    /// <inheritdoc />
    public override string Key => "DynamicDNSCertificateRefresh";

    /// <inheritdoc />
    public override string Description => "Requests a Let's Encrypt certificate when one is missing, no longer covers your addresses or expires within 30 days, and reloads or replaces other certificates as needed.";

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

        var status = await _proxy.RefreshCertificateAsync(cancellationToken).ConfigureAwait(false);
        if (status.Mode == CertificateMode.None)
        {
            progress.Report(100);
            return;
        }

        // Let's Encrypt failures are logged where they happen, with the reason and the next try.
        if (!status.Loaded && status.Mode != CertificateMode.Automatic)
        {
            _logger.LogError("Remote access certificate could not be loaded: {Error}", status.Error);
            _activity.Log("Remote Access could not load its certificate", "DynamicDNS.CertificateFailed", status.Error, LogLevel.Error);
        }
        else if (status.Loaded && status.NotAfterUtc is { } expires && expires - DateTime.UtcNow < _warnBefore)
        {
            var message = $"The certificate for {string.Join(", ", status.Names)} expires {expires:yyyy-MM-dd}. Renew it so remote access keeps working.";
            _logger.LogWarning("{Message}", message);
            _activity.Log("Remote Access certificate expires soon", "DynamicDNS.CertificateExpiring", message, LogLevel.Warning);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Seeds the default schedule only. Administrators change it under Scheduled Tasks.
        yield return Daily(TimeSpan.FromHours(3));
    }
}
