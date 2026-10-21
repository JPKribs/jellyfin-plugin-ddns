using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.DynamicDns.Utilities;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Keeps People history current: opens or downloads the location database at startup, records open sessions,
/// refused connections and internet visits every minute, keeps the server's own country current, and prunes old
/// history once a day.
/// </summary>
public sealed class PeopleHostedService : BackgroundService
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _pruneInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan _serverInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan _serverRetry = TimeSpan.FromMinutes(10);

    private readonly PeopleService _people;
    private readonly GeoIpService _geo;
    private readonly BlockFilter _blocks;
    private readonly PeopleStore _store;
    private readonly RepeatedProblem _loop = new();
    private readonly RepeatedProblem _locations = new();
    private readonly ServerLocation _server;
    private readonly AccessRecorder _access;
    private readonly BanList _bans;
    private readonly ActivityLogger _activity;
    private readonly ILogger<PeopleHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleHostedService"/> class.
    /// </summary>
    /// <param name="people">The People service.</param>
    /// <param name="geo">The location lookup.</param>
    /// <param name="blocks">The block list and country rule filter.</param>
    /// <param name="store">The history store.</param>
    /// <param name="server">The country this server is in.</param>
    /// <param name="access">The proxy's access recorder, holding internet visits.</param>
    /// <param name="bans">The ban list, whose held-back activity summary is written each minute.</param>
    /// <param name="activity">The activity log writer, for the hourly summary of refused connections.</param>
    /// <param name="logger">The logger.</param>
    public PeopleHostedService(PeopleService people, GeoIpService geo, BlockFilter blocks, PeopleStore store, ServerLocation server, AccessRecorder access, BanList bans, ActivityLogger activity, ILogger<PeopleHostedService> logger)
    {
        _activity = activity;
        _bans = bans;
        _people = people;
        _geo = geo;
        _blocks = blocks;
        _store = store;
        _server = server;
        _access = access;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Found at startup, before the activity log was ready; it is written there once now.
        if (FolderAccess.Problem.Length > 0)
        {
            _activity.Log("Remote Access cannot use its own files", "DynamicDNS.FolderAccess", FolderAccess.Problem, LogLevel.Error);
        }

        // The database loads in the background, so a slow or stalled download never holds up history.
        _ = Task.Run(() => EnsureLocationsAsync(stoppingToken), stoppingToken);

        var lastPrune = DateTime.MinValue;
        var refused = default(RefusalTotals);
        var refusedSince = DateTime.UtcNow;
        var lastServer = DateTime.MinValue;
        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            try
            {
                // Drained even when history is off, so the buffers never grow.
                var blocked = _blocks.Drain();
                var seen = _access.DrainSeen();
                _bans.FlushActivity();

                // Refusals are routine once rules are on, so both logs get one summary an hour, not one entry per
                // connection; the per-minute count is at debug level.
                refused += _blocks.TakeTotals();
                if (DateTime.UtcNow - refusedSince >= TimeSpan.FromHours(1))
                {
                    if (refused.Total > 0)
                    {
                        _logger.LogInformation("Refused {Count} internet connections in the last hour: {Detail}", refused.Total, refused.Describe());
                        _activity.Log(
                            $"Remote Access refused {refused.Total} internet connections in the last hour",
                            "DynamicDNS.ConnectionsRefused",
                            "Refused " + refused.Describe() + ". The Remote Access page lists the addresses.");
                    }

                    refused = default;
                    refusedSince = DateTime.UtcNow;
                }
                if (PeopleService.Recording)
                {
                    _store.RecordBlocked(blocked);
                    foreach (var visit in seen)
                    {
                        var location = NetworkClassifier.ParseKey(visit.Ip) is { } ip ? _geo.Lookup(ip) : GeoLocation.Unknown;
                        visit.CountryCode = location.CountryCode;
                        visit.Country = location.Country;
                    }

                    _store.RecordSeen(seen);
                }

                _people.Poll();

                var serverDue = _server.Location.CountryCode.Length > 0 ? _serverInterval : _serverRetry;
                if (_geo.IsLoaded && ServerCountryNeeded() && DateTime.UtcNow - lastServer > serverDue)
                {
                    lastServer = DateTime.UtcNow;
                    await _server.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                if (DateTime.UtcNow - lastPrune > _pruneInterval)
                {
                    _people.Prune();

                    // The key ring adds a new key every 90 days, written in the clear; it is wrapped here too.
                    if (Plugin.Instance is { } plugin)
                    {
                        KeyRingProtection.Protect(plugin.KeyRingDirectory, _logger);
                    }

                    lastPrune = DateTime.UtcNow;
                }

                _loop.Succeeded(() => _logger.LogInformation("Recording People history works again"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The loop runs every minute, so a lasting fault warns once.
                _loop.Failed(level => _logger.Log(level, ex, "Recording People history failed"));
            }
        }
        while (await NextTick(timer, stoppingToken).ConfigureAwait(false));
    }

    // The server's own country only matters to the country rule, and finding it asks outside services.
    private static bool ServerCountryNeeded()
        => Plugin.Instance?.ReadConfiguration(c => ProxyCountryRules.CountryRulesOn(c.Proxy)) ?? false;

    private async Task EnsureLocationsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _geo.EnsureAsync(PeopleService.RequiredLocation, stoppingToken).ConfigureAwait(false);
            _locations.Succeeded();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _locations.Failed(level => _logger.Log(level, ex, "Loading the location database failed"));
        }
    }

    private static async Task<bool> NextTick(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
