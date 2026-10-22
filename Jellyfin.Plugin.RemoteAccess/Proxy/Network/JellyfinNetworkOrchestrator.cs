using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
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
/// start never locks anyone out of Jellyfin: the problem is reported, and Undo puts the settings back. The one
/// exception is Only through the proxy, where Jellyfin listens on this machine alone: a proxy that is not
/// working at startup then gives Jellyfin its own addresses back on its own.
/// </summary>
public sealed class JellyfinNetworkOrchestrator : IDisposable
{
    private const string NetworkKey = "network";

    /// <summary>The copy of Jellyfin's network file from before the proxy first changed it, in Jellyfin's config folder.</summary>
    public const string BackupFileName = "network.before-remote-access.xml";
    private static readonly TimeSpan _verifyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan _verifyInterval = TimeSpan.FromSeconds(3);
    private static readonly Lazy<XmlSerializer> _networkSerializer = new(() => new XmlSerializer(typeof(NetworkConfiguration)));

    private readonly IConfigurationManager _configuration;
    private readonly ISystemManager _system;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ProxyPaths _paths;
    private readonly JsonFileStore<TakeoverState> _store;
    private readonly ActivityLogger? _activity;
    private readonly ILogger<JellyfinNetworkOrchestrator> _logger;

    // Trust, Undo, the loopback change and the startup check each read the state, change Jellyfin's settings and
    // write the state back. One at a time, so none of them works from a state another is halfway through changing.
    private readonly SemaphoreSlim _gate = new(1, 1);
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
    /// Gets a value indicating whether Jellyfin listens on this machine only because of Only through the proxy,
    /// so the proxy is the only way in from other machines.
    /// </summary>
    public bool IsBoundToLoopback
        => State is { Snapshot.BoundToLoopback: true, Phase: TakeoverPhase.PendingRestart or TakeoverPhase.Managed };

    /// <summary>Gets or sets the pause before a restart, so the API can answer first. Shortened by tests.</summary>
    internal TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

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
    public Task<TakeoverResult> ApplyAsync(ProxySettings settings, ProxyManager proxy, CancellationToken cancellationToken, Guid userId = default, bool checkProxy = true)
        => ApplyCoreAsync(settings, proxy, userId, checkProxy, onlyWhileManaged: false, cancellationToken);

    /// <summary>
    /// Applies a change to Only through the proxy, which is part of trusting the proxy. While Jellyfin trusts the
    /// proxy the change is applied at once, with a restart. Otherwise nothing happens: the setting waits for Trust
    /// the Proxy. Turning it on needs the proxy running and reaching Jellyfin.
    /// </summary>
    /// <param name="settings">The normalized proxy settings, with the new value.</param>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="userId">The administrator who asked, for the activity log.</param>
    /// <returns>The outcome. Restarting is false when nothing had to change now.</returns>
    public Task<TakeoverResult> ApplyLoopbackChangeAsync(ProxySettings settings, ProxyManager proxy, Guid userId = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ApplyCoreAsync(settings, proxy, userId, checkProxy: ProxyOptions.From(settings.Advanced).JellyfinLoopbackOnly, onlyWhileManaged: true, CancellationToken.None);
    }

    private async Task<TakeoverResult> ApplyCoreAsync(ProxySettings settings, ProxyManager proxy, Guid userId, bool checkProxy, bool onlyWhileManaged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(proxy);

        if (!settings.Enabled)
        {
            return new TakeoverResult(false, "Turn the proxy on and save first.", false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read again under the lock: an Undo or another change may have finished while this one waited.
            if (onlyWhileManaged)
            {
                var phase = _store.Load().Phase;
                if (phase == TakeoverPhase.PendingRestart)
                {
                    return new TakeoverResult(false, "Jellyfin is still restarting from the last change to trusting the proxy. Try again once it is back.", false);
                }

                if (phase != TakeoverPhase.Managed)
                {
                    return new TakeoverResult(true, string.Empty, false);
                }
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

            // Read after the checks, which can take a while, so the values written are the ones current now.
            return CommitCore(settings, _configuration.GetNetworkConfiguration(), userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the takeover once every check has passed: the snapshot first, then Jellyfin's settings, then a
    /// restart. Separate from the checks so the order of the writes can be tested without a proxy.
    /// </summary>
    /// <param name="settings">The normalized proxy settings.</param>
    /// <param name="network">Jellyfin's network configuration, which is changed and saved.</param>
    /// <param name="userId">The administrator who asked, for the activity log.</param>
    /// <returns>The outcome.</returns>
    internal TakeoverResult Commit(ProxySettings settings, NetworkConfiguration network, Guid userId = default)
    {
        _gate.Wait();
        try
        {
            return CommitCore(settings, network, userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private TakeoverResult CommitCore(ProxySettings settings, NetworkConfiguration network, Guid userId)
    {
        // A state file that is there but cannot be read may hold the original settings. Taking it for "not
        // trusted yet" would capture the already managed values as the original, and Undo could never put the
        // real ones back.
        if (!TryReadState(out var state))
        {
            return new TakeoverResult(false, UnreadableState(), false);
        }

        if (state.Snapshot is null || state.Phase is TakeoverPhase.Unmanaged or TakeoverPhase.Degraded)
        {
            // From the saved file, not the instance Jellyfin keeps in memory, which an earlier change in this run
            // may already have altered.
            state.Snapshot = NetworkTakeover.Capture(ReadSavedNetwork() ?? network);
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
    /// Keeps the addresses Jellyfin gives its clients in step with the proxy's hostnames while Jellyfin trusts
    /// the proxy. Only the entries trusting wrote are replaced. Jellyfin reads these entries again whenever its
    /// network settings are saved, so no restart is needed.
    /// </summary>
    /// <param name="settings">The normalized proxy settings, with the new hostnames.</param>
    /// <param name="userId">The administrator who changed them, for the activity log.</param>
    /// <returns>True when Jellyfin's settings were changed.</returns>
    public bool UpdatePublishedAddresses(ProxySettings settings, Guid userId = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _gate.Wait();
        try
        {
            var state = _store.Load();
            if (state.Phase != TakeoverPhase.Managed || state.Snapshot is not { } snapshot)
            {
                return false;
            }

            // A name that cannot be advertised, such as a wildcard alone, leaves the earlier entries in place.
            var external = NetworkTakeover.ExternalEntry(settings);
            var local = snapshot.BoundToLoopback ? NetworkTakeover.LocalEntries(settings).ToList() : new List<string>();
            var externalChanged = external.Length > 0 && !string.Equals(external, snapshot.WrittenExternal, StringComparison.OrdinalIgnoreCase);
            var localChanged = local.Count > 0 && !local.SequenceEqual(snapshot.AllWrittenLocal, StringComparer.OrdinalIgnoreCase);
            if (!externalChanged && !localChanged)
            {
                return false;
            }

            var network = _configuration.GetNetworkConfiguration();
            var entries = network.PublishedServerUriBySubnet.ToList();
            if (externalChanged)
            {
                if (!string.IsNullOrEmpty(snapshot.WrittenExternal))
                {
                    entries.RemoveAll(e => string.Equals(e, snapshot.WrittenExternal, StringComparison.OrdinalIgnoreCase));
                }

                entries.RemoveAll(e => string.Equals(e, external, StringComparison.OrdinalIgnoreCase));
                entries.Add(external);
                snapshot.WrittenExternal = external;
            }

            if (localChanged)
            {
                var old = snapshot.AllWrittenLocal.ToList();
                entries.RemoveAll(e => old.Contains(e, StringComparer.OrdinalIgnoreCase) || local.Contains(e, StringComparer.OrdinalIgnoreCase));
                entries.InsertRange(0, local);
                snapshot.WrittenLocal = local;
                snapshot.WrittenInternal = string.Empty;
            }

            // The record first, as for every change, so Undo knows which entries are the plugin's.
            _store.Save(state);
            network.PublishedServerUriBySubnet = entries.ToArray();
            _configuration.SaveConfiguration(NetworkKey, network);
            _logger.LogInformation("Updated the addresses Jellyfin gives its clients to follow the proxy's hostnames");
            _activity?.Log(
                "Remote Access updated Jellyfin's published address",
                "DynamicDNS.TakeoverAddressUpdated",
                "Jellyfin now gives its clients the address " + external.Replace("external=", string.Empty, StringComparison.OrdinalIgnoreCase) + ".",
                userId: userId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
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
        _gate.Wait();
        try
        {
            return RestoreCore(reason, restart, userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private TakeoverResult RestoreCore(string reason, bool restart, Guid userId)
    {
        if (!TryReadState(out var state))
        {
            return new TakeoverResult(false, UnreadableState(), false);
        }

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
    /// mode checks that Jellyfin answers through the proxy. A failed check is reported. Jellyfin keeps its own
    /// ports, so it stays reachable, and a restart would not fix a busy port. With Only through the proxy,
    /// Jellyfin listens on this machine alone, so a failed check also gives it its own addresses back.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="cancellationToken">Cancelled when Jellyfin shuts down.</param>
    /// <returns>A task.</returns>
    public Task OnStartupAsync(ProxyManager proxy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return OnStartupCoreAsync(token => VerifyAsync(proxy, token), ReadSettings, cancellationToken);
    }

    /// <summary>
    /// The startup check, with the check through the proxy passed in so it can be tested without one.
    /// </summary>
    /// <param name="verify">Checks Jellyfin answers through the proxy: null when it does, otherwise why not.</param>
    /// <param name="settings">Reads the saved proxy settings.</param>
    /// <param name="cancellationToken">Cancelled when Jellyfin shuts down.</param>
    /// <returns>A task.</returns>
    internal async Task OnStartupCoreAsync(Func<CancellationToken, Task<string?>> verify, Func<ProxySettings> settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(settings);
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

        var problem = await verify(cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read again under the lock: an Undo while the check ran leaves nothing to record.
            state = _store.Load();
            if (state.Phase is not (TakeoverPhase.PendingRestart or TakeoverPhase.Managed))
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
                if (state.Snapshot is { BoundToLoopback: true })
                {
                    ReleaseLoopbackCore(settings(), state, problem);
                }
            }
            else if (wasPending)
            {
                _activity?.Log("Remote Access set Jellyfin to trust the proxy", "DynamicDNS.TakeoverCompleted", "The proxy started and Jellyfin answered through it.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Jellyfin listens on this machine only and the proxy, its only way in, is not working: its own addresses
    // come back and the setting is turned off, so the server can be reached again after one restart. Trusting the
    // proxy otherwise stays as it was, so fixing the proxy and turning the setting back on is all it takes.
    // Caller holds _gate, and the state is saved with the binding still recorded.
    private void ReleaseLoopbackCore(ProxySettings settings, TakeoverState state, string problem)
    {
        var snapshot = state.Snapshot!;
        var unbound = ProxyRuntimeFactory.Clone(settings);
        unbound.Advanced ??= new ProxyAdvancedSettings();
        unbound.Advanced.JellyfinLoopbackOnly = false;

        var network = _configuration.GetNetworkConfiguration();
        NetworkTakeover.ApplyManaged(network, unbound, snapshot, wasBound: true, snapshot.AllWrittenLocal.ToList());
        _configuration.SaveConfiguration(NetworkKey, network);

        var written = NetworkTakeover.ExternalEntry(unbound);
        if (written.Length > 0 || string.IsNullOrEmpty(snapshot.WrittenExternal))
        {
            snapshot.WrittenExternal = written;
        }

        snapshot.BoundToLoopback = false;
        snapshot.WrittenInternal = string.Empty;
        snapshot.WrittenLocal = new List<string>();
        const string Released = "Only through the proxy was turned off, so Jellyfin listens on its own addresses again.";
        state.LastError = problem + " " + Released;
        state.LastErrorUtc = DateTime.UtcNow;
        _store.Save(state);

        // The setting follows, so it never claims what Jellyfin is not doing.
        Plugin.Instance?.MutateConfiguration(c =>
        {
            if (c.Proxy?.Advanced is not { JellyfinLoopbackOnly: true } advanced)
            {
                return false;
            }

            advanced.JellyfinLoopbackOnly = false;
            return true;
        });

        _logger.LogError("The proxy is not working and Jellyfin only listened on this machine, so Jellyfin's own addresses were put back. Restarting Jellyfin");
        _activity?.Log(
            "Remote Access gave Jellyfin its own addresses back",
            "DynamicDNS.TakeoverLoopbackReleased",
            "Jellyfin could not be reached through the proxy. " + Released + " Jellyfin is restarting. Fix the proxy, then turn the setting on again.",
            LogLevel.Error);
        ScheduleRestart();
    }

    private static ProxySettings ReadSettings()
    {
        var settings = Plugin.Instance?.ReadConfiguration(c => ProxyRuntimeFactory.Clone(c.Proxy)) ?? new ProxySettings();
        ProxySettingsValidator.Normalize(settings);
        return settings;
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

    // The store reads a damaged or locked file as empty. Here that difference matters, so the file is read
    // directly: false when it is there but could not be read.
    private bool TryReadState(out TakeoverState state)
    {
        state = new TakeoverState();
        try
        {
            if (!File.Exists(_paths.StateFile))
            {
                return true;
            }

            var json = File.ReadAllText(_paths.StateFile);
            if (!string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<TakeoverState>(json) is { } read)
            {
                state = read;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not read {Path}: {Message}", _paths.StateFile, ex.Message);
        }

        return false;
    }

    private string UnreadableState()
        => "Remote Access could not read its record of Jellyfin's network settings, so nothing was changed. Try again in a moment. If this keeps happening, check the file " + _paths.StateFile + ".";

    // Jellyfin's network settings as saved on disk, or null when the file is missing or cannot be read.
    private NetworkConfiguration? ReadSavedNetwork()
    {
        var path = Path.Join(_configuration.CommonApplicationPaths?.ConfigurationDirectoryPath ?? string.Empty, NetworkKey + ".xml");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return _networkSerializer.Value.Deserialize(reader) as NetworkConfiguration;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException)
        {
            _logger.LogDebug("Could not read Jellyfin's saved network settings, using the loaded ones: {Message}", ex.Message);
            return null;
        }
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
            try
            {
                // A short pause lets the API answer the dashboard before the server goes down.
                await Task.Delay(RestartDelay).ConfigureAwait(false);
                _system.Restart();
            }
            catch (Exception ex)
            {
                // A restart that did not happen must not stop the next one from being asked for.
                Interlocked.Exchange(ref _restartScheduled, 0);
                _logger.LogError(ex, "Jellyfin could not be restarted. Restart it by hand so the network change takes effect");
                _activity?.Log(
                    "Remote Access could not restart Jellyfin",
                    "DynamicDNS.RestartFailed",
                    "Restart Jellyfin by hand so the change to its network settings takes effect.",
                    LogLevel.Error);
            }
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
