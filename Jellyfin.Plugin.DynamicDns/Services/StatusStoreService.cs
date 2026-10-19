using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Services;

/// <summary>
/// Persists runtime status to a JSON file beside the encryption keys in the plugin configuration folder.
/// Keeping it out of the config XML means the configuration is rewritten only when the user changes a
/// setting, not on every update run.
/// </summary>
public sealed class StatusStoreService
{
    private readonly JsonFileStore<StatusData> _store;
    private readonly ILogger<StatusStoreService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusStoreService"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public StatusStoreService(IApplicationPaths paths, ILogger<StatusStoreService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _store = new JsonFileStore<StatusData>(Path.Join(paths.PluginConfigurationsPath, "Jellyfin.Plugin.DynamicDns.Status.json"), logger);
    }

    /// <summary>
    /// Reads the stored status, or a fresh empty status when none exists or it cannot be read.
    /// </summary>
    /// <returns>The status data.</returns>
    public StatusData Load() => _store.Load();

    /// <summary>
    /// Writes the status to disk. The write is atomic, and a failure is logged rather than thrown so a
    /// status write never fails the update run it records.
    /// </summary>
    /// <param name="data">The status to persist.</param>
    public void Save(StatusData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            _store.Save(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the status store.");
        }
    }

    /// <summary>
    /// Loads the stored status, applies <paramref name="mutate"/>, and saves the result under the store's
    /// lock, so a change made by another caller in between is never overwritten by a stale copy. A failure
    /// is logged rather than thrown so a status write never fails the update run it records.
    /// </summary>
    /// <param name="mutate">The change to apply to the stored status.</param>
    public void Update(Action<StatusData> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        try
        {
            _store.Update(mutate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the status store.");
        }
    }

    /// <summary>
    /// Removes the status of the given records, so each starts over with no backoff and no last pushed
    /// address. Loading, removing, and saving happen under the store's lock, and nothing is written when
    /// none of the records had status. A failure is logged rather than thrown.
    /// </summary>
    /// <param name="recordIds">The ids of the records to reset.</param>
    public void RemoveRecords(IEnumerable<string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        var ids = recordIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            _store.UpdateIf(data =>
            {
                var removed = false;
                foreach (var id in ids)
                {
                    removed |= data.Records.Remove(id);
                }

                return removed;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the status store.");
        }
    }
}
