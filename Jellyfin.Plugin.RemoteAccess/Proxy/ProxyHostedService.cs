using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Starts the proxy with Jellyfin and stops it with Jellyfin. Start-up work runs in the background because
/// Jellyfin's own server starts listening only after every hosted service has started.
/// </summary>
public sealed class ProxyHostedService : IHostedService, IDisposable
{
    private readonly ProxyManager _proxy;
    private readonly JellyfinNetworkOrchestrator _orchestrator;
    private readonly ILogger<ProxyHostedService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _startup = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyHostedService"/> class.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="orchestrator">The port takeover orchestrator.</param>
    /// <param name="logger">The logger.</param>
    public ProxyHostedService(ProxyManager proxy, JellyfinNetworkOrchestrator orchestrator, ILogger<ProxyHostedService> logger)
    {
        _proxy = proxy;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        var stopping = _stopping.Token;
        _startup = Task.Run(
            async () =>
            {
                try
                {
                    await _proxy.ApplyAsync(stopping).ConfigureAwait(false);
                    await _orchestrator.OnStartupAsync(_proxy, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reverse proxy start-up failed");
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _proxy.ShutdownAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        // A save through Jellyfin's generic plugin endpoint; the dashboard's own endpoint applies directly.
        _ = Task.Run(async () =>
        {
            try
            {
                await _proxy.ApplyAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Applying reverse proxy settings failed");
            }
        });
    }
}
