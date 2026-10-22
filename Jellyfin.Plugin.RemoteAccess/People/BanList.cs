using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.DynamicDns.Utilities;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Bans internet addresses that fail to sign in too often, for a set time. Failures are counted in memory over
/// a sliding window; bans are kept in the history database so a restart does not lift them. Local and trusted
/// networks never reach it, because the proxy only reports internet failures.
/// </summary>
public sealed class BanList : IFailureTracker
{
    // Keys counted at once, about 12 MB at most; beyond it, the one counted longest ago is dropped. An IPv6
    // failure counts under three keys (its /64, /56 and /48), so this holds 20,000 IPv6 addresses' worth.
    private const int MaxTracked = 60_000;

    // Bans written to Jellyfin's activity log one by one each minute, before the rest are summed up.
    private const int ActivityPerMinute = 5;

    private static readonly int[] _tierFactors = { 1, 3, 6 };

    // A ban on a /56 or /48 also shuts out everyone else in it, such as a carrier's or a university's customers,
    // so it is short; an attacker who carries on is banned again when it ends.
    private const int WideBanMinutes = 15;

    // A /56 or /48 banned again within a day gets twice its last ban, up to the configured ban length, or up
    // to a day when bans last until lifted. The short first ban spares the neighbours, the doubling stops an
    // attacker who simply waits each one out.
    private const int MaxWideBanMinutes = 24 * 60;
    private static readonly TimeSpan _repeatWindow = TimeSpan.FromDays(1);

    // Wide networks remembered for doubling, bounded like the failure counts.
    private const int MaxRepeats = 10_000;
    private readonly ConcurrentDictionary<string, (DateTime Last, int Minutes)> _repeats = new(StringComparer.Ordinal);
    private readonly PeopleStore _store;
    private readonly GeoIpService _geo;
    private readonly ActivityLogger? _activity;
    private readonly RepeatedProblem _load = new();
    private readonly RepeatedProblem _save = new();
    private readonly ILogger<BanList> _logger;
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Ban> _bans = new(StringComparer.Ordinal);
    private readonly object _activityLock = new();
    private long _activityMinute;
    private int _activityCount;
    private int _suppressed;
    private int _loaded;
    private long _nextLoadTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="BanList"/> class.
    /// </summary>
    /// <param name="store">The history store, which keeps the bans.</param>
    /// <param name="geo">The location lookup.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">The activity log writer, which records each ban.</param>
    public BanList(PeopleStore store, GeoIpService geo, ILogger<BanList> logger, ActivityLogger? activity = null)
    {
        _store = store;
        _geo = geo;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool RecordFailure(IPAddress client, ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BanFailures <= 0)
        {
            return false;
        }

        // Answers still arriving for requests sent before the ban count for nothing, and must not ban again.
        if (IsBanned(client))
        {
            return false;
        }

        // Every tier counts every failure, so a /64 ban never hides failures from its /56 and /48. The widest
        // tier past its threshold is the one banned.
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromMinutes(options.BanWindowMinutes);
        (string Key, int Count)? reached = null;
        foreach (var (key, threshold) in Tiers(client, options.BanFailures))
        {
            var count = Count(key, now, window);
            if (count >= threshold)
            {
                reached = (key, count);
            }
        }

        if (reached is not { } ban)
        {
            return false;
        }

        // Only the banned tier starts over. The wider ones keep counting: an attacker who moves to a fresh /64
        // after each ban is caught by its /56 and /48 totals, which a reset here would wipe.
        _failures.TryRemove(ban.Key, out _);
        Ban(client, ban.Key, ban.Count, now, options);
        return true;
    }

    /// <summary>
    /// The ban in force on an address, whether it is on the address itself or on a wider network holding it.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>The ban's key, or null when the address is not banned.</returns>
    public string? BanFor(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bans = Bans;
        foreach (var key in NetworkClassifier.TierKeys(address))
        {
            if (Active(bans, key))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the summary of bans held back from the activity log, once the minute they fell in has passed.
    /// Called every minute, so a flood that stops is still summed up.
    /// </summary>
    public void FlushActivity() => LogActivity(DateTime.UtcNow, null, null);

    // An IPv6 client holding a /56 or /48 can move to a fresh /64 before each ban. Failures are also counted
    // for those wider networks, which are banned whole once their failures pass a few addresses' worth.
    private static IEnumerable<(string Key, int Threshold)> Tiers(IPAddress client, int failures)
        => NetworkClassifier.TierKeys(client).Select((key, i) => (key, failures * _tierFactors[i]));

    private int Count(string key, DateTime now, TimeSpan window)
    {
        if (!_failures.ContainsKey(key) && _failures.Count >= MaxTracked)
        {
            // Full: forget only counts that have aged out of the window. Wiping everything would let one
            // attacker reset every other attacker's count by cycling through addresses.
            foreach (var stale in _failures.Where(p => { lock (p.Value) { return p.Value.TrueForAll(t => now - t > window); } }).Select(p => p.Key).ToList())
            {
                _failures.TryRemove(stale, out _);
            }

            // Still full: drop the tenth whose counting started longest ago, in one pass. Refusing new addresses
            // instead would let an attacker who keeps the tracker full guess from one more address without limit;
            // dropping one at a time would scan the whole tracker on every guess.
            if (_failures.Count >= MaxTracked)
            {
                foreach (var oldest in _failures.Select(p => (p.Key, Start: Started(p.Value))).OrderBy(p => p.Start).Take(MaxTracked / 10).Select(p => p.Key).ToList())
                {
                    _failures.TryRemove(oldest, out _);
                }
            }
        }

        var times = _failures.GetOrAdd(key, _ => new List<DateTime>());
        lock (times)
        {
            times.RemoveAll(t => now - t > window);
            times.Add(now);
            return times.Count;
        }
    }

    private void Ban(IPAddress client, string key, int count, DateTime now, ProxyOptions options)
    {
        var minutes = options.BanMinutes;
        if (!string.Equals(key, NetworkClassifier.GroupKey(client), StringComparison.Ordinal))
        {
            minutes = WideMinutes(key, now, options.BanMinutes);
        }

        var location = _geo.IsLoaded ? _geo.Lookup(client) : GeoLocation.Unknown;
        var ban = new Ban
        {
            Ip = key,
            CountryCode = location.CountryCode,
            Country = location.Country,
            Failures = count,
            BannedUtc = now,
            UntilUtc = minutes > 0 ? now.AddMinutes(minutes) : null
        };
        Bans[key] = ban;
        Save(() => _store.SaveBan(ban));
        var where = location.Country.Length > 0 ? location.Country : "an unknown country";
        var length = minutes > 0 ? $"for {minutes} minutes" : "until the ban is lifted";
        if (LogActivity(now, "Remote Access banned " + key, $"{count} failed sign-ins from {where}. Banned {length}."))
        {
            _logger.LogWarning("Banned {Key} in {Country} after {Failures} failed sign-ins", key, where, count);
        }
    }

    /// <summary>
    /// How long a /56 or /48 is banned: short the first time, doubled each time it is banned again within a day.
    /// </summary>
    /// <param name="key">The network's key.</param>
    /// <param name="now">The time, UTC.</param>
    /// <param name="banMinutes">The configured ban length, zero for until lifted.</param>
    /// <returns>The minutes, never zero.</returns>
    internal int WideMinutes(string key, DateTime now, int banMinutes)
    {
        var cap = banMinutes > 0 ? banMinutes : MaxWideBanMinutes;
        var minutes = Math.Min(cap, WideBanMinutes);
        if (_repeats.TryGetValue(key, out var last) && now - last.Last < _repeatWindow)
        {
            minutes = (int)Math.Min(cap, Math.Max(minutes, last.Minutes * 2L));
        }

        if (!_repeats.ContainsKey(key) && _repeats.Count >= MaxRepeats)
        {
            foreach (var stale in _repeats.Where(p => now - p.Value.Last >= _repeatWindow).Select(p => p.Key).ToList())
            {
                _repeats.TryRemove(stale, out _);
            }

            if (_repeats.Count >= MaxRepeats)
            {
                foreach (var oldest in _repeats.OrderBy(p => p.Value.Last).Take(MaxRepeats / 10).Select(p => p.Key).ToList())
                {
                    _repeats.TryRemove(oldest, out _);
                }
            }
        }

        _repeats[key] = (now, minutes);
        return minutes;
    }

    /// <summary>
    /// Reads the saved bans now, before the proxy listens, so they apply from the first connection. A failed
    /// read is retried on later use, as before.
    /// </summary>
    public void Preload() => _ = Bans;

    // A few bans a minute are logged one by one; past that, they are summed up, so an attack spread over many
    // addresses cannot bury everything else in Jellyfin's activity log.
    // Returns whether this ban was written out on its own. A null name only flushes the summary.
    private bool LogActivity(DateTime now, string? name, string? overview)
    {
        var minute = now.Ticks / TimeSpan.TicksPerMinute;
        int suppressed;
        bool log;
        lock (_activityLock)
        {
            suppressed = 0;
            if (minute != _activityMinute)
            {
                suppressed = _suppressed;
                _activityMinute = minute;
                _activityCount = 0;
                _suppressed = 0;
            }

            log = name is not null && _activityCount < ActivityPerMinute;
            if (log)
            {
                _activityCount++;
            }
            else if (name is not null)
            {
                _suppressed++;
            }
        }

        if (suppressed > 0)
        {
            _logger.LogWarning("Banned {Count} more addresses in the last minute", suppressed);
            _activity?.Log($"Remote Access banned {suppressed} more addresses", "DynamicDNS.AddressBanned", "Too many bans to list one by one. The Remote Access page shows them all.", LogLevel.Warning);
        }

        if (log)
        {
            _activity?.Log(name!, "DynamicDNS.AddressBanned", overview ?? string.Empty, LogLevel.Warning);
        }

        return log;
    }

    private static DateTime Started(List<DateTime> times)
    {
        lock (times)
        {
            return times.Count > 0 ? times[0] : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Whether an address is banned right now.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>True when banned.</returns>
    public bool IsBanned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bans = Bans;
        if (bans.IsEmpty)
        {
            return false;
        }

        return NetworkClassifier.TierKeys(address).Any(key => Active(bans, key));
    }

    private static bool Active(ConcurrentDictionary<string, Ban> bans, string? key)
    {
        if (key is null || !bans.TryGetValue(key, out var ban))
        {
            return false;
        }

        if (ban.ActiveAt(DateTime.UtcNow))
        {
            return true;
        }

        // Only this expired entry is removed; a fresh ban written meanwhile stays. The database row is left for
        // the daily prune, so no write happens on the connection path.
        bans.TryRemove(new KeyValuePair<string, Ban>(ban.Ip, ban));
        return false;
    }

    /// <summary>
    /// The bans in force, newest first.
    /// </summary>
    /// <returns>The bans.</returns>
    public List<Ban> Active()
    {
        var now = DateTime.UtcNow;
        var bans = Bans;

        // Ended bans leave memory here; their rows are left for the daily prune.
        foreach (var ended in bans.Where(p => !p.Value.ActiveAt(now)).ToList())
        {
            bans.TryRemove(ended);
        }

        return bans.Values.OrderByDescending(b => b.BannedUtc).ToList();
    }

    /// <summary>
    /// Lifts every ban holding an address: its own, and any on its /56 or /48.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>The bans lifted.</returns>
    public List<string> UnbanAll(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return NetworkClassifier.TierKeys(address).Where(Unban).ToList();
    }

    /// <summary>
    /// Lifts a ban and forgets the address's recent failures.
    /// </summary>
    /// <param name="ip">The address.</param>
    /// <returns>True when it was banned.</returns>
    public bool Unban(string ip)
    {
        _failures.TryRemove(ip, out _);
        _repeats.TryRemove(ip, out _);
        var removed = Bans.TryRemove(ip, out _);
        if (removed)
        {
            Save(() => _store.DeleteBan(ip));
        }

        return removed;
    }

    // The bans in memory, read from the database on first use. A failed read is retried a minute later
    // rather than leaving every saved ban lifted until the next restart.
    private ConcurrentDictionary<string, Ban> Bans
    {
        get
        {
            if (Volatile.Read(ref _loaded) == 0)
            {
                Load();
            }

            return _bans;
        }
    }

    private void Load()
    {
        var now = DateTime.UtcNow;
        var next = Interlocked.Read(ref _nextLoadTicks);
        if (now.Ticks < next || Interlocked.CompareExchange(ref _nextLoadTicks, now.Ticks + TimeSpan.TicksPerMinute, next) != next)
        {
            return;
        }

        try
        {
            foreach (var ban in _store.GetBans().Where(b => b.ActiveAt(now)))
            {
                _bans.TryAdd(ban.Ip, ban);
            }

            Volatile.Write(ref _loaded, 1);
            _load.Succeeded(() => _logger.LogInformation("Read the saved bans"));
        }
        catch (Exception ex) when (ex is SqliteException or System.IO.IOException or InvalidOperationException)
        {
            // Retried each minute, so this warns once until it works.
            _load.Failed(level => _logger.Log(level, "Could not read the saved bans: {Message}", ex.Message));
        }
    }

    // A ban still holds in memory when the database cannot be written; it is only lost on restart.
    private void Save(Action write)
    {
        try
        {
            write();
            _save.Succeeded(() => _logger.LogInformation("Bans are being saved again"));
        }
        catch (Exception ex) when (ex is SqliteException or System.IO.IOException or InvalidOperationException)
        {
            _save.Failed(level => _logger.Log(level, "Could not save a ban: {Message}", ex.Message));
        }
    }
}
