using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Feeds YARP its routes and clusters from the runtime snapshot, and pushes changes live.
/// </summary>
/// <remarks>
/// Routes are selected by an internal header the policy middleware sets, not by YARP's own host matching,
/// so the route the policy checked is exactly the route that serves the request.
/// </remarks>
public sealed class ProxyConfigProvider : IProxyConfigProvider
{
    /// <summary>The route metadata key carrying whether the client's Host header is forwarded.</summary>
    public const string PreserveHostKey = "PreserveHost";

    /// <summary>How long a media stream may sit idle, long enough for a paused film.</summary>
    public static readonly TimeSpan MediaActivityTimeout = TimeSpan.FromHours(6);

    /// <summary>The cluster metadata key carrying a pinned backend certificate thumbprint.</summary>
    public const string PinnedThumbprintKey = "PinnedThumbprint";

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
        var routes = runtime.Routes
            .Select(r => Route(r.Id, r.ClusterId, r.PreserveHost))
            .ToList();
        routes.Add(Route(ProxyRuntime.JellyfinId, ProxyRuntime.JellyfinId, preserveHost: true));
        routes.Add(Route(ProxyRuntime.JellyfinMediaId, ProxyRuntime.JellyfinMediaId, preserveHost: true));
        return routes;
    }

    /// <summary>
    /// Builds the YARP clusters for a runtime.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <returns>The clusters.</returns>
    public static IReadOnlyList<ClusterConfig> BuildClusters(ProxyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ForwarderRequestConfig Request(RuntimeRoute? route) => new()
        {
            ActivityTimeout = TimeSpan.FromSeconds(EffectiveSettings.BackendActivityTimeoutSeconds(runtime, route)),
            AllowResponseBuffering = false,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        var clusters = runtime.Routes.Select(r => new ClusterConfig
        {
            ClusterId = r.ClusterId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["backend"] = new() { Address = r.Target.ToString() }
            },
            HttpRequest = Request(r),
            HttpClient = new HttpClientConfig { DangerousAcceptAnyServerCertificate = r.BackendTls == BackendTlsMode.Skip },
            Metadata = r.BackendTls == BackendTlsMode.Pinned
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [PinnedThumbprintKey] = r.PinnedThumbprint }
                : null
        }).ToList();

        clusters.Add(new ClusterConfig
        {
            ClusterId = ProxyRuntime.JellyfinId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                [ProxyRuntime.JellyfinId] = new() { Address = runtime.JellyfinBackend.ToString() }
            },
            HttpRequest = Request(null)
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
            HttpRequest = Request(null) with { ActivityTimeout = MediaActivityTimeout }
        });

        return clusters;
    }

    private static RouteConfig Route(string id, string clusterId, bool preserveHost) => new()
    {
        RouteId = id,
        ClusterId = clusterId,
        Match = new RouteMatch
        {
            Path = "{**catch-all}",
            Headers = new[]
            {
                new RouteHeader { Name = ProxyPolicyMiddleware.RouteHeader, Values = new[] { id }, Mode = HeaderMatchMode.ExactHeader, IsCaseSensitive = true }
            }
        },
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [PreserveHostKey] = preserveHost ? "true" : "false" }
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
