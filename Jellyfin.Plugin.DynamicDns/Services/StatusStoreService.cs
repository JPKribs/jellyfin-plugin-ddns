using System;
using System.IO;
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
}
