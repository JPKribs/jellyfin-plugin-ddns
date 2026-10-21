using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// A host lifetime that does nothing. The default console lifetime would hook process signals; the proxy
/// host lives inside Jellyfin and is started and stopped by the plugin, never by the process.
/// </summary>
public sealed class DetachedHostLifetime : IHostLifetime
{
    /// <inheritdoc />
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
