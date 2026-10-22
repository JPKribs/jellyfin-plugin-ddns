using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// This server's public addresses as the internet sees them, for the shared address and name checks. The DDNS
/// side already detects them on every update, so those are used. Only a server with no DDNS records detects
/// them here, at most every few minutes.
/// </summary>
public sealed class PublicAddresses : IDisposable
{
    private static readonly TimeSpan _keepStored = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _keepDetected = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _detectLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _statusWait = TimeSpan.FromSeconds(3);

    private readonly StatusStoreService _store;
    private readonly IPDetectionService _detection;
    private readonly ILogger<PublicAddresses> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string V4, string V6) _current = (string.Empty, string.Empty);
    private DateTime _readUtc = DateTime.MinValue;
    private DateTime _detectedUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicAddresses"/> class.
    /// </summary>
    /// <param name="store">Where the DDNS side keeps the addresses it detected.</param>
    /// <param name="detection">Detects the addresses when the DDNS side has none.</param>
    /// <param name="logger">The logger.</param>
    public PublicAddresses(StatusStoreService store, IPDetectionService detection, ILogger<PublicAddresses> logger)
    {
        _store = store;
        _detection = detection;
        _logger = logger;
    }

    /// <summary>Gets the addresses last known, without asking anything. Empty while unknown.</summary>
    public (string V4, string V6) Current => _current;

    /// <summary>
    /// Returns the addresses, refreshing them when they are old.
    /// </summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>The IPv4 and IPv6 address, each empty while unknown.</returns>
    public async Task<(string V4, string V6)> GetAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _readUtc < _keepStored)
        {
            return _current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _readUtc < _keepStored)
            {
                return _current;
            }

            var stored = _store.Load();
            if (stored.DetectedIPv4.Length > 0 || stored.DetectedIPv6.Length > 0)
            {
                _current = (stored.DetectedIPv4, stored.DetectedIPv6);
            }
            else if (DateTime.UtcNow - _detectedUtc >= _keepDetected)
            {
                _detectedUtc = DateTime.UtcNow;

                // Detection asks echo services and can take seconds. The status the page polls waits only briefly,
                // and a slower answer is there for the next one.
                var detect = Task.Run(async () => _current = await DetectAsync(CancellationToken.None).ConfigureAwait(false), CancellationToken.None);
                try
                {
                    await detect.WaitAsync(_statusWait, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogDebug("Still detecting the public address for the remote access checks");
                }
            }

            _readUtc = DateTime.UtcNow;
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string V4, string V6)> DetectAsync(CancellationToken cancellationToken)
    {
        // Only the detection settings are copied: the check never needs the records or their credentials.
        var config = Plugin.Instance?.ReadConfiguration(c => new PluginConfiguration
        {
            IPv4DetectionUrl = c.IPv4DetectionUrl,
            IPv6DetectionUrl = c.IPv6DetectionUrl,
            SkipInternalAddresses = c.SkipInternalAddresses,
            RequestTimeoutSeconds = c.RequestTimeoutSeconds
        }) ?? new PluginConfiguration();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_detectLimit);
        try
        {
            var found = await _detection.DetectAsync(config, needIPv4: true, needIPv6: true, limit.Token).ConfigureAwait(false);
            return (found.IPv4 ?? string.Empty, found.IPv6 ?? string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Detecting the public address for the remote access checks took too long");
            return _current;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Run in the background, so nothing else would ever see this. The checks say the address is unknown.
            _logger.LogDebug(ex, "Detecting the public address for the remote access checks failed");
            return _current;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
