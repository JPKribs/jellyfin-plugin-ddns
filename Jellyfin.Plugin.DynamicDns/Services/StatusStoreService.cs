using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.DynamicDns.Models;
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
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
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
        _path = Path.Join(paths.PluginConfigurationsPath, "Jellyfin.Plugin.DynamicDns.Status.json");
    }

    /// <summary>
    /// Reads the stored status, or a fresh empty status when none exists or it cannot be read.
    /// </summary>
    /// <returns>The status data.</returns>
    public StatusData Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var data = JsonSerializer.Deserialize<StatusData>(File.ReadAllText(_path), Options);
                    if (data is not null)
                    {
                        return data;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the status store, starting fresh.");
            }

            return new StatusData();
        }
    }

    /// <summary>
    /// Writes the status to disk.
    /// </summary>
    /// <param name="data">The status to persist.</param>
    public void Save(StatusData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(data, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write the status store.");
            }
        }
    }
}
