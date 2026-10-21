using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Tasks;

/// <summary>
/// Orders a new Let's Encrypt certificate now, past the usual timing rules. It has no schedule, so it only ever
/// runs when an administrator starts it: after a private key may have been exposed, or to replace a certificate
/// that is wrong in a way the automatic checks cannot see.
/// </summary>
public sealed class RequestCertificateTask : PluginScheduledTask
{
    private readonly ProxyManager _proxy;
    private readonly ILogger<RequestCertificateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestCertificateTask"/> class.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="logger">The logger.</param>
    public RequestCertificateTask(ProxyManager proxy, ILogger<RequestCertificateTask> logger)
    {
        _proxy = proxy;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string Name => "Request a New Certificate";

    /// <inheritdoc />
    public override string Key => "DynamicDNSCertificateRequest";

    /// <inheritdoc />
    public override string Description => "Orders a new Let's Encrypt certificate now, even when the current one is fine. Use it after a key may have been exposed. Let's Encrypt allows five of the same certificate a week.";

    /// <inheritdoc />
    public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);
        var (ok, message) = await _proxy.RequestCertificateAsync(cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            // Failures from Let's Encrypt itself are logged where they happen; this covers the ones before it.
            _logger.LogWarning("A new certificate was not issued: {Message}", message);
        }

        progress.Report(100);
    }
}
