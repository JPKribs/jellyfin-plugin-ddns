using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy;
using JPKribs.Jellyfin.Base;
using MaxMind.Db;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Looks up where internet addresses are, using DB-IP's free database kept on this server. Lookups never
/// leave the server. The database is downloaded when needed and refreshed by a scheduled task; a failed
/// download keeps the current database.
/// </summary>
public sealed class GeoIpService : IDisposable
{
    /// <summary>The attribution DB-IP's licence requires wherever its data is shown.</summary>
    public const string Attribution = "IP geolocation by DB-IP";

    // Decompressed size cap. The city database is about 130 MB; anything far larger is not a real database.
    private const long MaxDatabaseBytes = 512L * 1024 * 1024;

    // Long enough for the city edition on a slow line.
    private static readonly TimeSpan _downloadLimit = TimeSpan.FromMinutes(20);
    private const int CacheLimit = 10_000;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<GeoIpService> _logger;
    private readonly string _directory;
    private readonly SemaphoreSlim _download = new(1, 1);
    // Keyed by the address group, an IPv6 /64, so a client cycling addresses inside its /64 takes one entry
    // instead of clearing the cache for everyone. IPv4 addresses are their own group, so nothing is allocated.
    private readonly ConcurrentDictionary<IPAddress, GeoLocation> _cache = new();
    private Reader? _reader;
    private LocationDetail _loadedDetail = LocationDetail.Off;
    private string _error = string.Empty;

    // The failure last written to the activity log: a download failing the same way each day is one entry.
    private string _reportedError = string.Empty;
    private readonly ActivityLogger? _activity;
    private bool _downloading;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeoIpService"/> class.
    /// </summary>
    /// <param name="paths">The proxy data paths; the database lives beside the proxy's files.</param>
    /// <param name="http">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">The activity log writer, for download failures and recovery.</param>
    public GeoIpService(ProxyPaths paths, IHttpClientFactory http, ILogger<GeoIpService> logger, ActivityLogger? activity = null)
    {
        _activity = activity;
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Join(paths.DataDirectory, "GeoIP");
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// The download address for a database edition and month.
    /// </summary>
    /// <param name="detail">The edition.</param>
    /// <param name="month">The month.</param>
    /// <returns>The URL.</returns>
    public static Uri DownloadUrl(LocationDetail detail, DateTime month)
        => new(string.Create(
            CultureInfo.InvariantCulture,
            $"https://download.db-ip.com/free/dbip-{(detail == LocationDetail.City ? "city" : "country")}-lite-{month:yyyy-MM}.mmdb.gz"));

    /// <summary>Gets a value indicating whether a location database is loaded.</summary>
    public bool IsLoaded => Volatile.Read(ref _reader) is not null;

    /// <summary>
    /// Describes the database for the plugin's pages.
    /// </summary>
    /// <param name="detail">The configured detail.</param>
    /// <returns>The status.</returns>
    public GeoIpStatus GetStatus(LocationDetail detail)
    {
        var reader = Volatile.Read(ref _reader);
        return new GeoIpStatus
        {
            Detail = detail,
            Loaded = reader is not null && _loadedDetail == detail,
            BuiltUtc = reader?.Metadata.BuildDate,
            Error = _error,
            Downloading = _downloading
        };
    }

    /// <summary>
    /// Opens the database for the configured detail if it is on disk, and downloads it when it is not.
    /// Never throws; problems are kept for the status.
    /// </summary>
    /// <param name="detail">The configured detail.</param>
    /// <param name="cancellationToken">Cancels a download.</param>
    /// <returns>A task.</returns>
    public async Task EnsureAsync(LocationDetail detail, CancellationToken cancellationToken)
    {
        if (detail == LocationDetail.Off)
        {
            Swap(null, LocationDetail.Off);
            return;
        }

        if (_loadedDetail == detail && Volatile.Read(ref _reader) is not null)
        {
            return;
        }

        if (File.Exists(FilePath(detail)) && TryOpen(FilePath(detail), out var reader))
        {
            Swap(reader, detail);
            return;
        }

        await UpdateCoreAsync(detail, onlyIfMissing: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the newest database for the detail and switches to it. The current database stays in use
    /// if anything goes wrong.
    /// </summary>
    /// <param name="detail">The configured detail.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>True when a new database is in use.</returns>
    public Task<bool> UpdateAsync(LocationDetail detail, CancellationToken cancellationToken)
        => UpdateCoreAsync(detail, onlyIfMissing: false, cancellationToken);

    private async Task<bool> UpdateCoreAsync(LocationDetail detail, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        var updated = await DownloadAsync(detail, onlyIfMissing, cancellationToken).ConfigureAwait(false);
        var error = _error;
        if (error.Length > 0 && !string.Equals(error, _reportedError, StringComparison.Ordinal))
        {
            _reportedError = error;
            _activity?.Log("Remote Access could not update its location database", "DynamicDNS.LocationDatabaseFailed", error + " Country rules use the database already loaded, if any.", LogLevel.Warning);
        }
        else if (updated && _reportedError.Length > 0)
        {
            _reportedError = string.Empty;
            _activity?.Log("Remote Access updated its location database", "DynamicDNS.LocationDatabaseUpdated", "The download works again.");
        }

        return updated;
    }

    private async Task<bool> DownloadAsync(LocationDetail detail, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        if (detail == LocationDetail.Off)
        {
            return false;
        }

        await _download.WaitAsync(cancellationToken).ConfigureAwait(false);
        _downloading = true;
        var temp = FilePath(detail) + ".download";
        try
        {
            // Another caller may have loaded it while this one waited.
            if (onlyIfMissing && _loadedDetail == detail && Volatile.Read(ref _reader) is not null)
            {
                return true;
            }

            Directory.CreateDirectory(_directory);

            // DB-IP publishes at the start of each month, so early in a month the new file may not exist yet.
            var now = DateTime.UtcNow;
            _error = string.Empty;
            var downloaded = await TryDownloadAsync(DownloadUrl(detail, now), temp, cancellationToken).ConfigureAwait(false)
                || await TryDownloadAsync(DownloadUrl(detail, now.AddMonths(-1)), temp, cancellationToken).ConfigureAwait(false);
            if (!downloaded)
            {
                if (_error.Length == 0)
                {
                    _error = "DB-IP has no location database for this month or last month yet.";
                }

                return false;
            }

            if (!TryOpen(temp, out var probe) || !Validate(probe, detail))
            {
                probe?.Dispose();
                File.Delete(temp);
                _error = "The downloaded location database was not valid, so the current one is kept.";
                return false;
            }

            probe.Dispose();
            var target = FilePath(detail);
            if (File.Exists(target))
            {
                File.Move(target, target + ".previous", overwrite: true);
            }

            File.Move(temp, target, overwrite: true);

            // The settings may have changed during the download, for example Location set to Off. The file is
            // kept for later, but only the edition still wanted is loaded.
            if (PeopleService.RequiredLocation != detail)
            {
                return false;
            }

            if (!TryOpen(target, out var reader))
            {
                return false;
            }

            Swap(reader, detail);
            _error = string.Empty;
            _logger.LogInformation("Location database updated: {Detail}, built {Built:yyyy-MM-dd}", detail, reader.Metadata.BuildDate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidDatabaseException)
        {
            _error = "The location database could not be saved. " + ex.Message;
            _logger.LogWarning("Location database update failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            // A failed or cancelled download leaves a partial file, which can be hundreds of MB.
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Could not delete the partial location download: {Message}", ex.Message);
            }

            _downloading = false;
            _download.Release();
        }
    }

    /// <summary>
    /// Looks up an internet address. Local, loopback and private addresses are not looked up.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The location, or <see cref="GeoLocation.Unknown"/>.</returns>
    public GeoLocation Lookup(IPAddress? address)
    {
        var reader = Volatile.Read(ref _reader);
        if (reader is null || address is null)
        {
            return GeoLocation.Unknown;
        }

        var ip = NetworkClassifier.Normalize(address);
        if (!Models.IPAddressClassifier.IsPublic(ip))
        {
            return GeoLocation.Unknown;
        }

        var key = NetworkClassifier.Group(ip);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        GeoLocation location;
        try
        {
            location = Parse(reader.Find<Dictionary<string, object>>(ip));
        }
        // A damaged or hostile database file can fail in other ways inside the reader; a lookup only ever costs
        // the location, never the sign-in or connection it was for.
        catch (Exception ex) when (ex is InvalidDatabaseException or ObjectDisposedException or IndexOutOfRangeException
            or ArgumentException or InvalidOperationException or InvalidCastException or OverflowException or NullReferenceException)
        {
            return GeoLocation.Unknown;
        }

        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[key] = location;
        return location;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Swap(null, LocationDetail.Off);
        _download.Dispose();
    }

    /// <summary>
    /// Reads the country and city out of a database record.
    /// </summary>
    /// <param name="record">The record, or null when the address is not in the database.</param>
    /// <returns>The location.</returns>
    public static GeoLocation Parse(Dictionary<string, object>? record)
    {
        if (record is null)
        {
            return GeoLocation.Unknown;
        }

        static string Name(object? node)
            => node is Dictionary<string, object> map && map.TryGetValue("names", out var names)
                && names is Dictionary<string, object> byLanguage && byLanguage.TryGetValue("en", out var en)
                ? en as string ?? string.Empty
                : string.Empty;

        record.TryGetValue("country", out var country);
        record.TryGetValue("city", out var city);
        var code = country is Dictionary<string, object> c && c.TryGetValue("iso_code", out var iso) ? iso as string ?? string.Empty : string.Empty;
        return new GeoLocation(code, Name(country), Name(city));
    }

    private static bool Validate(Reader reader, LocationDetail detail)
    {
        var type = reader.Metadata.DatabaseType ?? string.Empty;
        var right = detail == LocationDetail.City
            ? type.Contains("City", StringComparison.OrdinalIgnoreCase)
            : type.Contains("Country", StringComparison.OrdinalIgnoreCase);

        // A well-known public address must resolve to a country.
        return right && Parse(reader.Find<Dictionary<string, object>>(IPAddress.Parse("8.8.8.8"))).CountryCode.Length == 2;
    }

    private string FilePath(LocationDetail detail)
        => Path.Join(_directory, detail == LocationDetail.City ? "dbip-city-lite.mmdb" : "dbip-country-lite.mmdb");

    private bool TryOpen(string path, out Reader reader)
    {
        try
        {
            reader = new Reader(path, FileAccessMode.MemoryMapped);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDatabaseException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Location database {Path} could not be opened: {Message}", path, ex.Message);
            reader = null!;
            return false;
        }
    }

    private void Swap(Reader? reader, LocationDetail detail)
    {
        var old = Interlocked.Exchange(ref _reader, reader);
        _loadedDetail = reader is null ? LocationDetail.Off : detail;
        _cache.Clear();

        // A lookup may still be reading the old database; give it a moment before releasing the file.
        if (old is not null)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
    }

    private async Task<bool> TryDownloadAsync(Uri url, string target, CancellationToken cancellationToken)
    {
        // The client's timeout stops at the headers when the body is streamed, so the whole download has its
        // own limit: a stalled connection must not hold the download lock forever.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_downloadLimit);
        var token = limit.Token;
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _error = $"The location database download failed with status {(int)response.StatusCode}.";
                return false;
            }

            var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using (var gzip = new GZipStream(body, CompressionMode.Decompress))
                {
                    var file = File.Create(target);
                    await using (file.ConfigureAwait(false))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await gzip.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxDatabaseBytes)
                            {
                                throw new InvalidDataException("The location database download is larger than any real database.");
                            }

                            await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _error = ex is OperationCanceledException
                ? "The location database download took too long and was stopped."
                : "The location database could not be downloaded. " + ex.Message;
            return false;
        }
    }
}
