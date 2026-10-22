using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Feeds YARP its routes and clusters from the runtime snapshot, and pushes changes live.
/// </summary>
/// <remarks>
/// Routes are selected by an internal header the policy middleware sets, not by YARP's own host matching,
/// so media and everything else reach Jellyfin through the cluster the policy picked.
/// </remarks>
public sealed class ProxyConfigProvider : IProxyConfigProvider
{
    /// <summary>How long a media stream may sit idle, long enough for a paused film.</summary>
    public static readonly TimeSpan MediaActivityTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// The most connections each Jellyfin cluster opens to Jellyfin. YARP otherwise opens one per request in
    /// flight without limit. This is far above what a household uses, open sessions and streams included, and
    /// requests past it wait for a free connection rather than fail.
    /// </summary>
    public const int JellyfinConnections = 256;

    private volatile Snapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyConfigProvider"/> class.
    /// </summary>
    /// <param name="runtime">The initial runtime.</param>
    public ProxyConfigProvider(ProxyRuntime runtime)
    {
        _snapshot = Create(runtime);
    }

    /// <inheritdoc />
    public IProxyConfig GetConfig() => _snapshot;

    /// <summary>
    /// Replaces the routes and clusters and tells YARP to reload.
    /// </summary>
    /// <param name="runtime">The new runtime.</param>
    public void Update(ProxyRuntime runtime)
    {
        var old = _snapshot;
        _snapshot = Create(runtime);
        old.SignalChange();
    }

    /// <summary>
    /// Builds the YARP routes for a runtime.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The routes.</returns>
    public static IReadOnlyList<RouteConfig> BuildRoutes(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new List<RouteConfig> { Route(ProxyRuntime.JellyfinId), Route(ProxyRuntime.JellyfinMediaId) };
    }

    /// <summary>
    /// Builds the YARP clusters for a runtime.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The clusters.</returns>
    public static IReadOnlyList<ClusterConfig> BuildClusters(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var request = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(EffectiveSettings.BackendActivityTimeoutSeconds(runtime)),
            AllowResponseBuffering = false,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        var clusters = new List<ClusterConfig>();
        clusters.Add(new ClusterConfig
        {
            ClusterId = ProxyRuntime.JellyfinId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                [ProxyRuntime.JellyfinId] = new() { Address = runtime.JellyfinBackend.ToString() }
            },
            HttpRequest = request,
            HttpClient = new HttpClientConfig { MaxConnectionsPerServer = JellyfinConnections }
        });

        // YARP's activity timer only restarts when a read or write completes, and a paused player stops
        // reading, which stalls the write. Media gets a long timer so a pause does not end the stream.
        clusters.Add(new ClusterConfig
        {
            ClusterId = ProxyRuntime.JellyfinMediaId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                [ProxyRuntime.JellyfinMediaId] = new() { Address = runtime.JellyfinBackend.ToString() }
            },
            HttpRequest = request with { ActivityTimeout = MediaActivityTimeout },
            HttpClient = new HttpClientConfig { MaxConnectionsPerServer = JellyfinConnections }
        });

        return clusters;
    }

    private static RouteConfig Route(string id) => new()
    {
        RouteId = id,
        ClusterId = id,
        Match = new RouteMatch
        {
            Path = "{**catch-all}",
            Headers = new[]
            {
                new RouteHeader { Name = ProxyPolicyMiddleware.RouteHeader, Values = new[] { id }, Mode = HeaderMatchMode.ExactHeader, IsCaseSensitive = true }
            }
        }
    };

    private static Snapshot Create(ProxyRuntime runtime) => new(BuildRoutes(runtime), BuildClusters(runtime));

    private sealed class Snapshot : IProxyConfig, IDisposable
    {
        private readonly CancellationTokenSource _changed = new();

        public Snapshot(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_changed.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void SignalChange()
        {
            _changed.Cancel();
            Dispose();
        }

        public void Dispose() => _changed.Dispose();
    }
}
