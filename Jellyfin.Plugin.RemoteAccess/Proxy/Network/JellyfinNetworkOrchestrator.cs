using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Network;

/// <summary>
/// Sets Jellyfin to trust the proxy, and undoes it. The only code that writes Jellyfin's network configuration.
/// Every change is preceded by a snapshot. Jellyfin keeps its own ports throughout, so a proxy that fails to
/// start never locks anyone out of Jellyfin: the problem is reported, and Undo puts the settings back.
/// </summary>
public sealed class JellyfinNetworkOrchestrator
{
    private const string NetworkKey = "network";

    /// <summary>The copy of Jellyfin's network file from before the proxy first changed it, in Jellyfin's config folder.</summary>
    public const string BackupFileName = "network.before-remote-access.xml";
    private static readonly TimeSpan _verifyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan _verifyInterval = TimeSpan.FromSeconds(3);

    private readonly IConfigurationManager _configuration;
    private readonly ISystemManager _system;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ProxyPaths _paths;
    private readonly JsonFileStore<TakeoverState> _store;
    private readonly ActivityLogger? _activity;
    private readonly ILogger<JellyfinNetworkOrchestrator> _logger;
    private int _restartScheduled;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinNetworkOrchestrator"/> class.
    /// </summary>
    /// <param name="configuration">Jellyfin's configuration manager.</param>
    /// <param name="system">Jellyfin's system manager, for restarts.</param>
    /// <param name="lifetime">Jellyfin's host lifetime, so restarts wait until startup has finished.</param>
    /// <param name="paths">The proxy data paths.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinNetworkOrchestrator(
        IConfigurationManager configuration,
        ISystemManager system,
        IHostApplicationLifetime lifetime,
        ProxyPaths paths,
        ActivityLogger? activity,
        ILogger<JellyfinNetworkOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _configuration = configuration;
        _system = system;
        _lifetime = lifetime;
        _paths = paths;
        _activity = activity;
        _logger = logger;
        _store = new JsonFileStore<TakeoverState>(paths.StateFile, logger);
    }

    /// <summary>Gets the persisted takeover state.</summary>
    public TakeoverState State => _store.Load();

    /// <summary>
    /// Puts Jellyfin's network settings back when the plugin is uninstalled. Static because uninstalling runs
    /// from the plugin class, outside the service container. Jellyfin asks for a restart after an uninstall,
    /// which is when the restored settings take effect.
    /// </summary>
    /// <param name="paths">The proxy data paths.</param>
    /// <param name="configuration">Jellyfin's configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public static void RestoreForUninstall(ProxyPaths paths, IConfigurationManager configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configuration);
        var store = new JsonFileStore<TakeoverState>(paths.StateFile, logger);
        var state = store.Load();
        if (state.Snapshot is null)
        {
            return;
        }

        var network = configuration.GetNetworkConfiguration();
        NetworkTakeover.Restore(network, state.Snapshot);
        configuration.SaveConfiguration(NetworkKey, network);
        store.Save(new TakeoverState());
        logger.LogInformation("Restored Jellyfin's network settings because the plugin is being uninstalled");
    }

    /// <summary>
    /// Sets Jellyfin to trust the proxy and restarts it, after checking the proxy is running and can reach it.
    /// Applying again while managed updates the managed values and keeps the original snapshot.
    /// </summary>
    /// <param name="settings">The normalized proxy settings.</param>
    /// <param name="proxy">The proxy manager, for the probe and the ports it already holds.</param>
    /// <param name="cancellationToken">Cancels the checks (not the restart once scheduled).</param>
    /// <param name="userId">The administrator who asked, for the activity log.</param>
    /// <param name="checkProxy">Whether the proxy must be running and reach Jellyfin first. Off only for turning
    /// Only through the proxy off, which gives Jellyfin its own addresses back and needs nothing of the proxy.</param>
    /// <returns>The outcome.</returns>
    public async Task<TakeoverResult> ApplyAsync(ProxySettings settings, ProxyManager proxy, CancellationToken cancellationToken, Guid userId = default, bool checkProxy = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(proxy);

        if (!settings.Enabled)
        {
            return new TakeoverResult(false, "Turn the proxy on and save first.", false);
        }

        if (File.Exists(_paths.DisableFlag))
        {
            return new TakeoverResult(false, $"Remove the recovery flag file first: {_paths.DisableFlag}", false);
        }

        var errors = ProxySettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            return new TakeoverResult(false, string.Join(" ", errors), false);
        }

        var options = ProxyOptions.From(settings.Advanced);
        var network = _configuration.GetNetworkConfiguration();
        var clash = new[] { options.HttpPort, options.HttpsPort }.Intersect(new[] { proxy.Jellyfin.HttpPort, proxy.Jellyfin.HttpsPort }).ToList();
        if (clash.Count > 0)
        {
            return new TakeoverResult(false, $"Port {string.Join(", ", clash)} is Jellyfin's own port. Give the proxy different ports.", false);
        }

        // Trusting a proxy that is not listening only costs a restart and gains nothing, for example when this
        // machine does not allow Jellyfin to use ports below 1024.
        if (checkProxy && !proxy.IsRunning)
        {
            var reason = await proxy.CheckRunningAsync(cancellationToken).ConfigureAwait(false);
            return new TakeoverResult(false, "The proxy is not running. " + (reason ?? string.Empty), false);
        }

        var probe = checkProxy ? await proxy.ProbeAsync(settings, cancellationToken).ConfigureAwait(false) : null;
        if (probe is not null)
        {
            return new TakeoverResult(false, probe, false);
        }

        return Commit(settings, network, userId);
    }

    /// <summary>
    /// Writes the takeover once every check has passed: the snapshot first, then Jellyfin's settings, then a
    /// restart. Separate from the checks so the order of the writes can be tested without a proxy.
    /// </summary>
    /// <param name="settings">The normalized proxy settings.</param>
    /// <param name="network">Jellyfin's network configuration, as read before the checks.</param>
    /// <param name="userId">The administrator who asked, for the activity log.</param>
    /// <returns>The outcome.</returns>
    internal TakeoverResult Commit(ProxySettings settings, NetworkConfiguration network, Guid userId = default)
    {
        var state = _store.Load();
        if (state.Snapshot is null || state.Phase is TakeoverPhase.Unmanaged or TakeoverPhase.Degraded)
        {
            state.Snapshot = NetworkTakeover.Capture(network);
            BackUpNetworkFile();
        }

        // The snapshot is saved before Jellyfin's settings change, so Undo always has something to put back.
        // Trusting again with only wildcard names writes no new entry and leaves the earlier one in place, so the
        // earlier one is still what Undo must remove.
        var written = NetworkTakeover.ExternalEntry(settings);
        if (written.Length > 0 || string.IsNullOrEmpty(state.Snapshot.WrittenExternal))
        {
            state.Snapshot.WrittenExternal = written;
        }

        // What binding to this machine only replaces is recorded before it happens; turning it off later puts
        // back what the earlier apply replaced, so that apply's record is kept for the call below.
        var wasBound = state.Snapshot.BoundToLoopback;
        var writtenLocal = state.Snapshot.AllWrittenLocal.ToList();
        var loopbackOnly = ProxyOptions.From(settings.Advanced).JellyfinLoopbackOnly;

        // Turning the option off, the record of the binding is kept until Jellyfin's settings are saved: a crash
        // in between must still leave Undo knowing the addresses to put back.
        var turningOff = wasBound && !loopbackOnly;
        if (!turningOff)
        {
            state.Snapshot.BoundToLoopback = loopbackOnly;
            state.Snapshot.WrittenInternal = string.Empty;
            state.Snapshot.WrittenLocal = loopbackOnly ? NetworkTakeover.LocalEntries(settings).ToList() : new List<string>();
        }

        state.Phase = TakeoverPhase.PendingRestart;
        state.AppliedUtc = DateTime.UtcNow;
        state.LastError = string.Empty;
        state.LastErrorUtc = null;
        _store.Save(state);

        NetworkTakeover.ApplyManaged(network, settings, state.Snapshot, wasBound, writtenLocal);
        _configuration.SaveConfiguration(NetworkKey, network);
        if (turningOff)
        {
            state.Snapshot.BoundToLoopback = false;
            state.Snapshot.WrittenInternal = string.Empty;
            state.Snapshot.WrittenLocal = new List<string>();
            _store.Save(state);
        }

        const string Message = "Jellyfin now trusts the proxy and keeps its own ports. Jellyfin is restarting.";
        _logger.LogInformation("Jellyfin now trusts the reverse proxy; restarting Jellyfin");
        _activity?.Log("Remote Access is setting Jellyfin to trust the proxy", "DynamicDNS.TakeoverStarted", Message, userId: userId);
        ScheduleRestart();
        return new TakeoverResult(true, Message, true);
    }

    /// <summary>
    /// Puts Jellyfin's original network settings back.
    /// </summary>
    /// <param name="reason">Why, for the logs.</param>
    /// <param name="restart">Whether to restart Jellyfin so the settings take effect.</param>
    /// <param name="userId">The administrator who asked, for the activity log; empty when automatic.</param>
    /// <returns>The outcome.</returns>
    public TakeoverResult Restore(string reason, bool restart, Guid userId = default)
    {
        var state = _store.Load();
        if (state.Snapshot is null)
        {
            if (state.Phase != TakeoverPhase.Unmanaged)
            {
                _store.Save(new TakeoverState());
            }

            return new TakeoverResult(false, "Jellyfin's network settings are not managed by the proxy.", false);
        }

        var network = _configuration.GetNetworkConfiguration();
        NetworkTakeover.Restore(network, state.Snapshot);
        _configuration.SaveConfiguration(NetworkKey, network);
        _store.Save(new TakeoverState());

        const string Message = "Jellyfin's network settings were put back.";
        _logger.LogInformation("{Message} {Reason}", Message, reason);
        _activity?.Log("Remote Access put Jellyfin's network settings back", "DynamicDNS.TakeoverRestored", Message + " " + reason, userId: userId);
        if (restart)
        {
            ScheduleRestart();
        }

        return new TakeoverResult(true, Message + (restart ? " Jellyfin is restarting." : string.Empty), restart);
    }

    /// <summary>
    /// Runs once per startup after the proxy has been started: honours the recovery flag, and in managed
    /// mode checks that Jellyfin answers through the proxy. A failed check is reported, not rolled back:
    /// Jellyfin keeps its own ports, so it stays reachable, and a restart would not fix a busy port.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="cancellationToken">Cancelled when Jellyfin shuts down.</param>
    /// <returns>A task.</returns>
    public async Task OnStartupAsync(ProxyManager proxy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var state = _store.Load();
        if (File.Exists(_paths.DisableFlag))
        {
            if (state.Snapshot is not null)
            {
                Restore("The recovery flag file is present.", restart: true);
            }

            return;
        }

        if (state.Phase is not (TakeoverPhase.PendingRestart or TakeoverPhase.Managed))
        {
            return;
        }

        var problem = await VerifyAsync(proxy, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var wasPending = state.Phase == TakeoverPhase.PendingRestart;
        state.Phase = TakeoverPhase.Managed;
        state.LastError = problem ?? string.Empty;
        state.LastErrorUtc = problem is null ? null : DateTime.UtcNow;
        _store.Save(state);
        if (problem is not null)
        {
            _logger.LogError("Jellyfin could not be reached through the proxy: {Problem}", problem);
            _activity?.Log("Remote Access could not reach Jellyfin through the proxy", "DynamicDNS.TakeoverCheckFailed", problem, LogLevel.Error);
        }
        else if (wasPending)
        {
            _activity?.Log("Remote Access set Jellyfin to trust the proxy", "DynamicDNS.TakeoverCompleted", "The proxy started and Jellyfin answered through it.");
        }
    }

    private static async Task<string?> VerifyAsync(ProxyManager proxy, CancellationToken cancellationToken)
    {
        // Jellyfin's own server starts listening after the plugin's services start, so keep asking for a while.
        var deadline = DateTime.UtcNow + _verifyTimeout;
        // A proxy that failed to bind will not retry on its own this run, so there is nothing to wait for.
        if (!proxy.IsRunning)
        {
            return await proxy.CheckRunningAsync(cancellationToken).ConfigureAwait(false);
        }

        string? problem = "The proxy did not start.";
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            problem = await proxy.CheckRunningAsync(cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                return null;
            }

            try
            {
                await Task.Delay(_verifyInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return problem;
    }

    // A copy of Jellyfin's network file from before the first change, for putting it back by hand if the plugin
    // cannot, such as after its files were deleted. An earlier copy is never replaced.
    private void BackUpNetworkFile()
    {
        var folder = _configuration.CommonApplicationPaths.ConfigurationDirectoryPath;
        var source = Path.Join(folder, NetworkKey + ".xml");
        var backup = Path.Join(folder, BackupFileName);
        try
        {
            if (File.Exists(source) && !File.Exists(backup))
            {
                File.Copy(source, backup);
                _logger.LogInformation("Saved a copy of Jellyfin's network settings to {Backup}", backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save a copy of Jellyfin's network settings: {Message}", ex.Message);
        }
    }

    private void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) == 1)
        {
            return;
        }

        void Fire() => _ = Task.Run(async () =>
        {
            // A short pause lets the API answer the dashboard before the server goes down.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _system.Restart();
        });

        if (_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            Fire();
        }
        else
        {
            _lifetime.ApplicationStarted.Register(Fire);
        }
    }
}
