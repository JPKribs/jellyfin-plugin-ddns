using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// What the router should forward: the proxy's ports to this machine, or nothing.
/// </summary>
/// <param name="Enabled">Whether the proxy is on and the administrator lets it open ports.</param>
/// <param name="LanAddress">This machine's LAN address, null when none was found.</param>
/// <param name="Ports">Each external port with the port the proxy listens on for it; empty while it is not listening.</param>
public sealed record RouterRequest(bool Enabled, IPAddress? LanAddress, IReadOnlyList<(int External, int Internal)> Ports)
{
    /// <summary>Gets the request that forwards nothing.</summary>
    public static RouterRequest Off { get; } = new(false, null, Array.Empty<(int, int)>());
}

/// <summary>
/// Opens the proxy's ports on the router the way Plex does: NAT-PMP first, since it answers or fails at once,
/// then UPnP. Forwards are leased for two hours and renewed every hour, and removed when the proxy stops or
/// the administrator turns this off. All router work runs in the background and one run at a time.
/// </summary>
public sealed class RouterPortMapper : IAsyncDisposable
{
    /// <summary>The lease asked for, in seconds.</summary>
    public const int LeaseSeconds = 2 * 60 * 60;

    private static readonly TimeSpan _renewEvery = TimeSpan.FromHours(1);

    // Every request is bounded on its own, so this only stops a run that keeps finding more to do.
    private static readonly TimeSpan _runLimit = TimeSpan.FromSeconds(45);

    private static RouterPortMapper? _current;

    private readonly IReadOnlyList<IRouterClient> _clients;
    private readonly ILogger _logger;
    private readonly ActivityLogger? _activity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private readonly RepeatedProblem _problem = new();
    private readonly Timer _renew;
    private readonly object _statusLock = new();
    private RouterRequest _desired = RouterRequest.Off;
    private RouterStatus _status = new();
    private IRouterClient? _client;
    private List<PortMapping> _mapped = new();

    // What the activity log was last told, so a forward that keeps working or keeps failing the same way is one
    // entry, not one an hour.
    private string _reportedOpen = string.Empty;
    private string _reportedError = string.Empty;

    // Set once the plugin is uninstalled: nothing may be forwarded again, whatever the proxy asks for.
    private volatile bool _retired;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterPortMapper"/> class that talks to the real router.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">The activity log writer.</param>
    public RouterPortMapper(ILogger<RouterPortMapper> logger, ActivityLogger activity)
        : this(new IRouterClient[] { new NatPmpClient(), new UpnpClient() }, logger, activity)
    {
        Track(this);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterPortMapper"/> class with given router clients, for tests.
    /// </summary>
    /// <param name="clients">The ways to ask the router, tried in order.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">The activity log writer, or null for none.</param>
    internal RouterPortMapper(IReadOnlyList<IRouterClient> clients, ILogger logger, ActivityLogger? activity)
    {
        _clients = clients;
        _logger = logger;
        _activity = activity;
        _renew = new Timer(_ => _ = RunAsync(force: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets a copy of the current state, for the dashboard.</summary>
    public RouterStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status.Copy();
            }
        }
    }

    /// <summary>
    /// Sets what the router should forward and brings it in line in the background. Never blocks on the router.
    /// </summary>
    /// <param name="request">What to forward.</param>
    public void Apply(RouterRequest request)
    {
        Desire(request);
        _ = Task.Run(() => RunAsync(force: false), CancellationToken.None);
    }

    /// <summary>
    /// Finds the router and opens the ports now, even when they were opened a moment ago.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait, not the run.</param>
    /// <returns>The state afterwards.</returns>
    public async Task<RouterStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await RunAsync(force: true).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Status;
    }

    /// <summary>
    /// Removes every forward this server made and stops renewing them. Best effort: never throws.
    /// </summary>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Desire(RouterRequest.Off);
        try
        {
            await SyncAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Stopped waiting for the router forwards to be removed");
        }
    }

    /// <summary>
    /// Removes the forwards as the plugin is uninstalled, waiting a few seconds at most. Best effort: never throws.
    /// </summary>
    public static void RemoveForUninstall()
    {
        if (Volatile.Read(ref _current) is not { } mapper)
        {
            return;
        }

        mapper.Retire();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task.Run(() => mapper.StopAsync(timeout.Token), CancellationToken.None).Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException ex)
        {
            mapper._logger.LogDebug(ex, "Removing the router forwards during uninstall failed");
        }
    }

    // The uninstall hook is static and reaches the running mapper through these. Only the real mapper is
    // tracked, never one a test builds.
    private static void Track(RouterPortMapper mapper) => Volatile.Write(ref _current, mapper);

    private static void Untrack(RouterPortMapper mapper) => Interlocked.CompareExchange(ref _current, null, mapper);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        await _disposed.CancelAsync().ConfigureAwait(false);
        await _renew.DisposeAsync().ConfigureAwait(false);
        Untrack(this);
        foreach (var client in _clients.OfType<IDisposable>())
        {
            client.Dispose();
        }

        _gate.Dispose();
        _disposed.Dispose();
    }

    /// <summary>
    /// Stops this mapper from ever forwarding again, as when the plugin is uninstalled.
    /// </summary>
    internal void Retire()
    {
        _retired = true;
        Desire(RouterRequest.Off);
    }

    /// <summary>
    /// Sets what the router should forward without running.
    /// </summary>
    /// <param name="request">What to forward.</param>
    internal void Desire(RouterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = _retired ? RouterRequest.Off : request;
        Volatile.Write(ref _desired, request);
        lock (_statusLock)
        {
            _status.Enabled = request.Enabled;
        }
    }

    /// <summary>
    /// Brings the router in line with what is wanted.
    /// </summary>
    /// <param name="force">Whether to ask the router even when nothing changed since the last run, which renews the lease.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task.</returns>
    internal async Task SyncAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_runLimit);
            await SyncCoreAsync(force, limit.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The timer and background applies land here, where nothing may escape.
    private async Task RunAsync(bool force)
    {
        try
        {
            await SyncAsync(force, _disposed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Router port forwarding stopped before it finished");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Router port forwarding stopped during shutdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cancellation is handled above, so this logs every other failure.
            _logger.LogWarning(ex, "Router port forwarding failed");
        }
    }

    private async Task SyncCoreAsync(bool force, CancellationToken cancellationToken)
    {
        var desired = Volatile.Read(ref _desired);
        var lan = desired.LanAddress;
        var wanted = desired.Enabled && lan is not null
            ? desired.Ports.Select(p => new PortMapping(p.External, p.Internal, lan, LeaseSeconds)).ToList()
            : new List<PortMapping>();

        // Forwards no longer wanted, such as ones to an old address or port, go first, so the new ones are free.
        await RemoveAsync(_mapped.Except(wanted).ToList(), cancellationToken).ConfigureAwait(false);

        if (!desired.Enabled || wanted.Count == 0)
        {
            ArmRenewal(false);
            _client = _mapped.Count == 0 ? null : _client;
            var error = desired.Enabled && lan is null && desired.Ports.Count > 0
                ? "This server's LAN address could not be found. Set it in the Advanced settings."
                : string.Empty;
            SetStatus(new RouterStatus { Enabled = desired.Enabled, Error = error });
            if (error.Length > 0)
            {
                Report(error, false);
                return;
            }

            // Nothing is asked of the router now, so an old problem is over without anything having recovered.
            _problem.Succeeded();
            _reportedError = string.Empty;
            _reportedOpen = string.Empty;
            return;
        }

        var current = Status;
        if (!force && current.Mapped && _mapped.Count == wanted.Count && !wanted.Except(_mapped).Any())
        {
            return;
        }

        ArmRenewal(true);
        var outcome = await MapAsync(lan!, wanted, cancellationToken).ConfigureAwait(false);

        if (outcome.Client is not null)
        {
            _client = outcome.Client;
            _mapped = outcome.Mapped;
        }

        var status = new RouterStatus
        {
            Enabled = true,
            Method = outcome.Client?.Method ?? string.Empty,
            Mapped = outcome.Mapped.Count == wanted.Count,
            Ports = outcome.Mapped.Select(m => m.ExternalPort).ToList(),
            LanAddress = lan!.ToString(),
            ExternalAddress = outcome.External?.ToString() ?? string.Empty,
            Error = outcome.Error,
            RenewedUtc = outcome.Mapped.Count > 0 ? DateTime.UtcNow : current.RenewedUtc
        };
        SetStatus(status);
        Report(outcome.Error, outcome.Client is null);
        if (outcome.Mapped.Count > 0)
        {
            ReportOpened(status);
        }
    }

    private async Task<(IRouterClient? Client, List<PortMapping> Mapped, IPAddress? External, string Error)> MapAsync(IPAddress lan, List<PortMapping> wanted, CancellationToken cancellationToken)
    {
        // The way that worked last time is asked first, so a router that speaks both keeps its forwards under one.
        // When it no longer answers, its forwards went with it, so nothing is left to remove there.
        var order = _client is null ? _clients : _clients.Where(c => ReferenceEquals(c, _client)).Concat(_clients.Where(c => !ReferenceEquals(c, _client))).ToList();
        var firstError = string.Empty;
        foreach (var client in order)
        {
            if (!await client.DiscoverAsync(lan, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var external = await client.GetExternalAddressAsync(cancellationToken).ConfigureAwait(false);
            var mapped = new List<PortMapping>();
            var taken = new List<int>();
            var failed = string.Empty;
            foreach (var mapping in wanted)
            {
                var result = await client.AddAsync(mapping, cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case PortMapOutcome.Mapped:
                        mapped.Add(mapping);
                        break;
                    case PortMapOutcome.Conflict:
                        taken.Add(mapping.ExternalPort);
                        break;
                    default:
                        failed = failed.Length > 0 ? failed : result.Error;
                        break;
                }
            }

            var error = taken.Count > 0 ? TakenSentence(taken) : failed;

            // A router that took some ports, or said another device holds them, is the router. One that refused
            // everything for another reason may also speak the next way, which is then tried.
            if (mapped.Count > 0 || taken.Count > 0)
            {
                return (client, mapped, external, error);
            }

            firstError = firstError.Length > 0 ? firstError : error;
        }

        return firstError.Length > 0
            ? (null, new List<PortMapping>(), null, firstError)
            : (null, new List<PortMapping>(), null, NoRouterSentence(wanted));
    }

    private async Task RemoveAsync(List<PortMapping> mappings, CancellationToken cancellationToken)
    {
        if (_client is null || mappings.Count == 0)
        {
            return;
        }

        foreach (var mapping in mappings)
        {
            try
            {
                await _client.DeleteAsync(mapping, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lease runs out on its own, so a forward left behind closes within two hours.
                _logger.LogDebug(ex, "Removing the router forward for port {Port} failed", mapping.ExternalPort);
            }

            _mapped.Remove(mapping);
        }

        _logger.LogInformation("Removed the router forwards for port {Ports}", Ports(mappings.Select(m => m.ExternalPort)));
    }

    private void ArmRenewal(bool on)
    {
        try
        {
            _renew.Change(on ? _renewEvery : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown: nothing is left to renew.
            _logger.LogDebug("Not scheduling a router renewal during shutdown");
        }
    }

    private void SetStatus(RouterStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }

    private void Report(string error, bool noRouter)
    {
        if (error.Length == 0)
        {
            _problem.Succeeded(() => _logger.LogInformation("The router forwards the proxy's ports again"));
            _reportedError = string.Empty;
            return;
        }

        _problem.Failed(level => _logger.Log(level, "Opening ports on the router failed: {Error}", error));
        if (string.Equals(error, _reportedError, StringComparison.Ordinal))
        {
            return;
        }

        _reportedError = error;
        _activity?.Log(
            noRouter ? "Remote Access found no router that opens ports" : "Remote Access could not open ports on your router",
            noRouter ? "DynamicDNS.RouterNotFound" : "DynamicDNS.RouterPortsFailed",
            error,
            LogLevel.Warning);
    }

    private void ReportOpened(RouterStatus status)
    {
        var key = status.Method + " " + status.LanAddress + " " + string.Join(',', status.Ports);
        if (string.Equals(key, _reportedOpen, StringComparison.Ordinal))
        {
            return;
        }

        _reportedOpen = key;
        var ports = Ports(status.Ports);
        _logger.LogInformation("The router forwards port {Ports} to {Address} using {Method}", ports, status.LanAddress, status.Method);
        _activity?.Log(
            $"Remote Access opened {(status.Ports.Count == 1 ? "port" : "ports")} {ports} on your router",
            "DynamicDNS.RouterPortsOpened",
            $"They forward to {status.LanAddress} using {status.Method}. The router keeps them while Jellyfin runs.");
    }

    // "80", "80 and 443".
    private static string Ports(IEnumerable<int> ports)
    {
        var list = ports.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    /// <summary>
    /// Says which ports another device holds.
    /// </summary>
    /// <param name="ports">The external ports.</param>
    /// <returns>A plain sentence.</returns>
    internal static string TakenSentence(IReadOnlyCollection<int> ports)
        => ports.Count == 1
            ? PortMapResult.Taken(ports.First()).Error
            : $"The router already forwards ports {Ports(ports)} to another device.";

    /// <summary>
    /// Says that no router answered.
    /// </summary>
    /// <param name="wanted">The forwards asked for.</param>
    /// <returns>A plain sentence.</returns>
    internal static string NoRouterSentence(IEnumerable<PortMapping> wanted)
    {
        var ports = wanted.Select(m => m.ExternalPort).ToList();
        return $"No router answered a request to open ports. Turn on UPnP or NAT-PMP in the router's settings, or forward {(ports.Count == 1 ? "port" : "ports")} {Ports(ports)} to this server by hand.";
    }
}
