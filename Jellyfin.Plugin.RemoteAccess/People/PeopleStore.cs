using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// The People history: who was seen from which address, and every sign-in attempt. Kept in a small SQLite
/// file beside the proxy's data. Each call opens its own connection; the volume is a few writes a minute.
/// </summary>
public sealed class PeopleStore
{
    /// <summary>The place filter value for your own network.</summary>
    public const string LocalPlace = "local";

    private const int CurrentVersion = 6;
    private const int MinReaderVersion = 1;

    // SQLite's codes for a damaged file and for one that is not a database.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    // Row caps that bound the file, about 20 MB in all, however many addresses an attacker uses.
    private const int MaxAddresses = 50_000;
    private const int MaxSignIns = 100_000;
    private const int ShrinkAfterRows = 10_000;

    private readonly string _path;
    private readonly ILogger<PeopleStore> _logger;
    private readonly object _schemaLock = new();
    private bool _ready;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleStore"/> class.
    /// </summary>
    /// <param name="paths">The proxy data paths.</param>
    /// <param name="logger">The logger.</param>
    public PeopleStore(ProxyPaths paths, ILogger<PeopleStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Join(paths.DataDirectory, "people.db");
        _logger = logger;
    }

    /// <summary>
    /// Records a user seen from an address, merging with what is already known for that user, address,
    /// device and hostname.
    /// </summary>
    /// <param name="o">The observation.</param>
    public void RecordObservation(Observation o)
    {
        ArgumentNullException.ThrowIfNull(o);
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO observation (user_id, user_name, ip, host, device_id, device_name, client, is_local, country_code, country, city, first_seen, last_seen, grp)
            VALUES ($user_id, $user_name, $ip, $host, $device_id, $device_name, $client, $is_local, $country_code, $country, $city, $seen, $seen, $grp)
            ON CONFLICT (user_id, ip, device_id, host) DO UPDATE SET
                user_name = excluded.user_name,
                device_name = excluded.device_name,
                client = excluded.client,
                is_local = excluded.is_local,
                country_code = CASE WHEN excluded.country_code <> '' THEN excluded.country_code ELSE observation.country_code END,
                country = CASE WHEN excluded.country <> '' THEN excluded.country ELSE observation.country END,
                city = CASE WHEN excluded.city <> '' THEN excluded.city ELSE observation.city END,
                last_seen = MAX(observation.last_seen, excluded.last_seen)
            """;
        Add(cmd, "$user_id", o.UserId);
        // What a client reports about itself is capped like a sign-in's, so a client sending huge names cannot
        // grow the file, and the rows are capped like the other address tables.
        Add(cmd, "$user_name", Cap(o.UserName));
        Add(cmd, "$ip", o.Ip);
        Add(cmd, "$host", Cap(o.Host));
        Add(cmd, "$device_id", Cap(o.DeviceId));
        Add(cmd, "$device_name", Cap(o.DeviceName));
        Add(cmd, "$client", Cap(o.Client));
        Add(cmd, "$is_local", o.IsLocal ? 1 : 0);
        Add(cmd, "$grp", Group(o.Ip));
        Add(cmd, "$country_code", o.CountryCode);
        Add(cmd, "$country", o.Country);
        Add(cmd, "$city", o.City);
        Add(cmd, "$seen", Ticks(o.LastSeenUtc));
        cmd.ExecuteNonQuery();
        Trim(c, tx, "observation");
        tx.Commit();
    }

    /// <summary>
    /// Records a sign-in attempt.
    /// </summary>
    /// <param name="a">The attempt.</param>
    public void RecordAttempt(SignInAttempt a)
    {
        ArgumentNullException.ThrowIfNull(a);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sign_in (time, username, user_id, success, ip, host, client, device_name, is_local, country_code, country, city, grp)
            VALUES ($time, $username, $user_id, $success, $ip, $host, $client, $device_name, $is_local, $country_code, $country, $city, $grp)
            """;
        Add(cmd, "$time", Ticks(a.TimeUtc));
        // These come from the client's sign-in request, so they are capped before they are kept.
        Add(cmd, "$username", Cap(a.Username));
        Add(cmd, "$grp", Group(a.Ip));
        Add(cmd, "$user_id", a.UserId);
        Add(cmd, "$success", a.Success ? 1 : 0);
        Add(cmd, "$ip", a.Ip);
        Add(cmd, "$host", a.Host);
        Add(cmd, "$client", Cap(a.Client));
        Add(cmd, "$device_name", Cap(a.DeviceName));
        Add(cmd, "$is_local", a.IsLocal ? 1 : 0);
        Add(cmd, "$country_code", a.CountryCode);
        Add(cmd, "$country", a.Country);
        Add(cmd, "$city", a.City);
        cmd.ExecuteNonQuery();

        // Ids only grow, so everything this far behind the newest is past the cap.
        using var trim = c.CreateCommand();
        trim.CommandText = "DELETE FROM sign_in WHERE id <= (SELECT MAX(id) FROM sign_in) - $max";
        Add(trim, "$max", MaxSignIns);
        trim.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds refused connections to the history, merging with what is already known for each address.
    /// </summary>
    /// <param name="attempts">The refusals, one per address.</param>
    public void RecordBlocked(IReadOnlyCollection<BlockedAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count == 0)
        {
            return;
        }

        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO blocked (ip, country_code, country, reason, attempts, first_seen, last_seen)
            VALUES ($ip, $country_code, $country, $reason, $attempts, $first, $last)
            ON CONFLICT (ip) DO UPDATE SET
                country_code = CASE WHEN excluded.country_code <> '' THEN excluded.country_code ELSE country_code END,
                country = CASE WHEN excluded.country <> '' THEN excluded.country ELSE country END,
                reason = excluded.reason,
                attempts = attempts + excluded.attempts,
                last_seen = MAX(last_seen, excluded.last_seen)
            """;
        var ip = cmd.Parameters.Add("$ip", SqliteType.Text);
        var code = cmd.Parameters.Add("$country_code", SqliteType.Text);
        var country = cmd.Parameters.Add("$country", SqliteType.Text);
        var reason = cmd.Parameters.Add("$reason", SqliteType.Text);
        var count = cmd.Parameters.Add("$attempts", SqliteType.Integer);
        var first = cmd.Parameters.Add("$first", SqliteType.Integer);
        var last = cmd.Parameters.Add("$last", SqliteType.Integer);
        foreach (var a in attempts)
        {
            ip.Value = a.Ip;
            code.Value = a.CountryCode;
            country.Value = a.Country;
            reason.Value = a.Reason.ToString();
            count.Value = a.Attempts;
            first.Value = Ticks(a.FirstUtc);
            last.Value = Ticks(a.LastUtc);
            cmd.ExecuteNonQuery();
        }

        Trim(c, tx, "blocked");
        tx.Commit();
    }

    /// <summary>
    /// Adds internet visits to the history, merging with what is already known for each address.
    /// </summary>
    /// <param name="visits">The visits, one per address.</param>
    public void RecordSeen(IReadOnlyCollection<SeenVisit> visits)
    {
        ArgumentNullException.ThrowIfNull(visits);
        if (visits.Count == 0)
        {
            return;
        }

        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO seen (ip, host, device, country_code, country, requests, first_seen, last_seen)
            VALUES ($ip, $host, $device, $country_code, $country, $requests, $first, $last)
            ON CONFLICT (ip) DO UPDATE SET
                host = excluded.host,
                device = CASE WHEN excluded.device <> '' THEN excluded.device ELSE device END,
                country_code = CASE WHEN excluded.country_code <> '' THEN excluded.country_code ELSE country_code END,
                country = CASE WHEN excluded.country <> '' THEN excluded.country ELSE country END,
                requests = requests + excluded.requests,
                last_seen = MAX(last_seen, excluded.last_seen)
            """;
        var ip = cmd.Parameters.Add("$ip", SqliteType.Text);
        var host = cmd.Parameters.Add("$host", SqliteType.Text);
        var device = cmd.Parameters.Add("$device", SqliteType.Text);
        var code = cmd.Parameters.Add("$country_code", SqliteType.Text);
        var country = cmd.Parameters.Add("$country", SqliteType.Text);
        var count = cmd.Parameters.Add("$requests", SqliteType.Integer);
        var first = cmd.Parameters.Add("$first", SqliteType.Integer);
        var last = cmd.Parameters.Add("$last", SqliteType.Integer);
        foreach (var v in visits)
        {
            ip.Value = v.Ip;
            host.Value = v.Host;
            device.Value = v.Device;
            code.Value = v.CountryCode;
            country.Value = v.Country;
            count.Value = v.Requests;
            first.Value = Ticks(v.FirstUtc);
            last.Value = Ticks(v.LastUtc);
            cmd.ExecuteNonQuery();
        }

        Trim(c, tx, "seen");
        tx.Commit();
    }

    // Keeps a table of addresses at its cap, dropping the ones seen longest ago. An attacker rotating through
    // addresses, an IPv6 /64 holds billions, would otherwise grow the file for the whole history window.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The table name is always a constant; values go in as parameters.")]
    private static void Trim(SqliteConnection c, SqliteTransaction tx, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE last_seen < (SELECT last_seen FROM {table} ORDER BY last_seen DESC LIMIT 1 OFFSET $max)";
        Add(cmd, "$max", MaxAddresses);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Lists what each address did, newest first: users, failed sign-ins, refused addresses, and internet
    /// addresses that reached the page but did none of those. Filters apply before the limit, so a flood of
    /// one kind never hides another.
    /// </summary>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="type">Only this kind, or null for every kind.</param>
    /// <param name="place">A two-letter country code, <see cref="LocalPlace"/> for your network, or empty for anywhere.</param>
    /// <returns>The rows.</returns>
    public List<ActivityRow> GetActivity(int limit, ActivityType? type = null, string? place = null)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var filter = new Filter(limit, place ?? string.Empty);
        using var c = Open();
        var rows = new List<ActivityRow>();
        if (type is null or ActivityType.User)
        {
            Read(
                c,
                """
                SELECT user_name, ip, device_name, client, host, country_code, country, is_local, last_seen, 1, city FROM observation
                WHERE ($local = 0 OR is_local = 1) AND ($code = '' OR (is_local = 0 AND country_code = $code))
                ORDER BY last_seen DESC LIMIT $limit
                """,
                filter,
                ActivityType.User,
                rows);
        }

        if (type is null or ActivityType.Failure)
        {
            // With a single MAX(), SQLite takes the other columns from that newest row, so a row never mixes
            // values from different attempts. Usernames are matched the way Jellyfin does, ignoring case.
            Read(
                c,
                """
                SELECT username, ip, device_name, client, host, country_code, country, is_local, MAX(time), COUNT(*), city FROM sign_in
                WHERE success = 0 AND ($local = 0 OR is_local = 1) AND ($code = '' OR (is_local = 0 AND country_code = $code))
                GROUP BY ip, username COLLATE NOCASE ORDER BY MAX(time) DESC LIMIT $limit
                """,
                filter,
                ActivityType.Failure,
                rows);
        }

        if (type is null or ActivityType.Blocked)
        {
            Read(
                c,
                """
                SELECT '', ip, '', reason, '', country_code, country, 0, last_seen, attempts, '' FROM blocked
                WHERE $local = 0 AND ($code = '' OR country_code = $code)
                ORDER BY last_seen DESC LIMIT $limit
                """,
                filter,
                ActivityType.Blocked,
                rows);
        }

        if (type is null or ActivityType.Seen)
        {
            Read(
                c,
                """
                SELECT '', ip, device, '', host, country_code, country, 0, last_seen, requests, '' FROM seen
                WHERE $local = 0 AND ($code = '' OR country_code = $code)
                AND NOT EXISTS (SELECT 1 FROM observation o WHERE o.grp = seen.ip OR o.ip = seen.ip)
                AND NOT EXISTS (SELECT 1 FROM sign_in s WHERE s.grp = seen.ip OR s.ip = seen.ip)
                AND NOT EXISTS (SELECT 1 FROM blocked b WHERE b.ip = seen.ip)
                ORDER BY last_seen DESC LIMIT $limit
                """,
                filter,
                ActivityType.Seen,
                rows);
        }

        return rows.OrderByDescending(r => r.LastUtc).Take(limit).ToList();
    }

    /// <summary>
    /// Counts the people seen from each country, for the map. Local addresses have no country and are left out.
    /// </summary>
    /// <returns>One total per country, most people first.</returns>
    public List<CountryTotal> GetPeopleByCountry()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT country_code, MAX(country), COUNT(DISTINCT user_id), MAX(last_seen)
            FROM observation WHERE is_local = 0 AND country_code <> ''
            GROUP BY country_code ORDER BY COUNT(DISTINCT user_id) DESC
            """;
        return ReadTotals(cmd);
    }

    /// <summary>
    /// Counts failed sign-ins from each country, for the map.
    /// </summary>
    /// <returns>One total per country, most attempts first.</returns>
    public List<CountryTotal> GetFailedByCountry()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT country_code, MAX(country), COUNT(*), MAX(time)
            FROM sign_in WHERE success = 0 AND is_local = 0 AND country_code <> ''
            GROUP BY country_code ORDER BY COUNT(*) DESC
            """;
        return ReadTotals(cmd);
    }

    /// <summary>
    /// Counts the refused addresses from each country, for the map.
    /// </summary>
    /// <returns>One total per country, most addresses first.</returns>
    public List<CountryTotal> GetBlockedByCountry()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT country_code, MAX(country), COUNT(*), MAX(last_seen)
            FROM blocked WHERE country_code <> ''
            GROUP BY country_code ORDER BY COUNT(*) DESC
            """;
        return ReadTotals(cmd);
    }

    /// <summary>
    /// Deletes everything older than the history window, and bans that have ended.
    /// </summary>
    /// <param name="keepDays">How many days to keep.</param>
    /// <returns>How many rows were removed.</returns>
    public int Prune(int keepDays)
    {
        var cutoff = Ticks(DateTime.UtcNow.AddDays(-Math.Max(1, keepDays)));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM observation WHERE last_seen < $cutoff; DELETE FROM sign_in WHERE time < $cutoff; DELETE FROM blocked WHERE last_seen < $cutoff; DELETE FROM seen WHERE last_seen < $cutoff; DELETE FROM ban WHERE until > 0 AND until < $now;";
        Add(cmd, "$cutoff", cutoff);
        Add(cmd, "$now", Ticks(DateTime.UtcNow));
        var removed = cmd.ExecuteNonQuery();
        if (removed >= ShrinkAfterRows)
        {
            // The file never shrinks on its own; after a large prune, such as the end of an attack, it is rewritten.
            using var shrink = c.CreateCommand();
            shrink.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            shrink.ExecuteNonQuery();
        }

        return removed;
    }

    /// <summary>
    /// Saves a ban, replacing any earlier one for the same address.
    /// </summary>
    /// <param name="ban">The ban.</param>
    public void SaveBan(Ban ban)
    {
        ArgumentNullException.ThrowIfNull(ban);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO ban (ip, country_code, country, failures, banned, until)
            VALUES ($ip, $country_code, $country, $failures, $banned, $until)
            """;
        Add(cmd, "$ip", ban.Ip);
        Add(cmd, "$country_code", ban.CountryCode);
        Add(cmd, "$country", ban.Country);
        Add(cmd, "$failures", ban.Failures);
        Add(cmd, "$banned", Ticks(ban.BannedUtc));
        Add(cmd, "$until", ban.UntilUtc is { } until ? Ticks(until) : 0L);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes the ban on an address.
    /// </summary>
    /// <param name="ip">The address.</param>
    public void DeleteBan(string ip)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM ban WHERE ip = $ip";
        Add(cmd, "$ip", ip);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Lists the saved bans, expired ones included.
    /// </summary>
    /// <returns>The bans.</returns>
    public List<Ban> GetBans()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ip, country_code, country, failures, banned, until FROM ban";
        using var r = cmd.ExecuteReader();
        var list = new List<Ban>();
        while (r.Read())
        {
            var until = r.GetInt64(5);
            list.Add(new Ban
            {
                Ip = r.GetString(0),
                CountryCode = r.GetString(1),
                Country = r.GetString(2),
                Failures = (int)r.GetInt64(3),
                BannedUtc = FromTicks(r.GetInt64(4)),
                UntilUtc = until > 0 ? FromTicks(until) : null
            });
        }

        return list;
    }

    /// <summary>
    /// Deletes all history.
    /// </summary>
    public void DeleteAll()
    {
        using (var c = Open())
        {
            // Deleted rows stay readable in free pages and the write-ahead log until the file is rewritten.
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM observation; DELETE FROM sign_in; DELETE FROM blocked; DELETE FROM seen; VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }

        // Copies set aside after a failed upgrade hold the same history.
        var folder = Path.GetDirectoryName(_path);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var copy in Directory.EnumerateFiles(folder, Path.GetFileName(_path) + ".*-*"))
        {
            try
            {
                File.Delete(copy);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Could not delete the old history copy {File}: {Message}", copy, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Could not delete the old history copy {File}: {Message}", copy, ex.Message);
            }
        }
    }

    // Each query returns the same eleven columns: user, ip, device, client (or block reason), host, country code,
    // country, is local, last time, count, city.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the constant queries in GetActivity are passed; values go in as parameters.")]
    private static void Read(SqliteConnection c, string sql, Filter filter, ActivityType type, List<ActivityRow> rows)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Add(cmd, "$limit", filter.Limit);
        Add(cmd, "$local", filter.Local ? 1 : 0);
        Add(cmd, "$code", filter.Local ? string.Empty : filter.Place.ToUpperInvariant());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var device = r.GetString(2);
            var client = r.GetString(3);
            rows.Add(new ActivityRow
            {
                Type = type,
                User = r.GetString(0),
                Ip = r.GetString(1),
                Device = type == ActivityType.Blocked ? string.Empty : string.Join(", ", new[] { device, client }.Where(x => x.Length > 0)),
                Host = r.GetString(4),
                CountryCode = r.GetString(5),
                Country = r.GetString(6),
                City = r.GetString(10),
                IsLocal = r.GetInt64(7) == 1,
                LastUtc = FromTicks(r.GetInt64(8)),
                Count = (int)r.GetInt64(9),
                Reason = type == ActivityType.Blocked && Enum.TryParse<BlockReason>(client, out var reason) ? reason : null
            });
        }
    }

    private static List<CountryTotal> ReadTotals(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<CountryTotal>();
        while (r.Read())
        {
            list.Add(new CountryTotal
            {
                CountryCode = r.GetString(0),
                Country = r.GetString(1),
                Count = r.GetInt32(2),
                LastUtc = FromTicks(r.GetInt64(3))
            });
        }

        return list;
    }

    private static string Cap(string? value) => value is null ? string.Empty : value.Length <= 256 ? value : value[..256];

    private static long Ticks(DateTime utc) => utc.ToUniversalTime().Ticks;

    private static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);

    private static void Add(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value);

    private static void CreateSchema(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE observation (
                user_id TEXT NOT NULL,
                user_name TEXT NOT NULL,
                ip TEXT NOT NULL,
                host TEXT NOT NULL,
                device_id TEXT NOT NULL,
                device_name TEXT NOT NULL,
                client TEXT NOT NULL,
                is_local INTEGER NOT NULL,
                country_code TEXT NOT NULL,
                country TEXT NOT NULL,
                city TEXT NOT NULL,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL,
                PRIMARY KEY (user_id, ip, device_id, host));
            CREATE INDEX observation_last_seen ON observation (last_seen);
            CREATE TABLE sign_in (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time INTEGER NOT NULL,
                username TEXT NOT NULL,
                user_id TEXT NOT NULL,
                success INTEGER NOT NULL,
                ip TEXT NOT NULL,
                host TEXT NOT NULL,
                client TEXT NOT NULL,
                device_name TEXT NOT NULL,
                is_local INTEGER NOT NULL,
                country_code TEXT NOT NULL,
                country TEXT NOT NULL,
                city TEXT NOT NULL);
            CREATE INDEX sign_in_time ON sign_in (time);
            """;
        cmd.ExecuteNonQuery();
        CreateBlocked(connection);
        CreateSeen(connection);
        CreateBans(connection);
        CreateAddressIndexes(connection);
        CreateGroupColumns(connection);
    }

    // Version 2 added the addresses the block list and country rules refused, version 3 the internet addresses
    // that reached the page, version 4 the bans for failed sign-ins, version 5 indexes by address, and version 6
    // the address group of each user and sign-in, which the Seen and refused lists are kept by.
    private static bool Migrate(DbConnection connection, int fromVersion)
    {
        if (fromVersion is < 1 or > 5)
        {
            return false;
        }

        if (fromVersion < 2)
        {
            CreateBlocked(connection);
        }

        if (fromVersion < 3)
        {
            CreateSeen(connection);
        }

        if (fromVersion < 4)
        {
            CreateBans(connection);
        }

        if (fromVersion < 5)
        {
            CreateAddressIndexes(connection);
        }

        CreateGroupColumns(connection);
        return true;
    }

    // Rows written before version 6 keep an empty group and are matched by address instead.
    private static void CreateGroupColumns(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE observation ADD COLUMN grp TEXT NOT NULL DEFAULT '';
            ALTER TABLE sign_in ADD COLUMN grp TEXT NOT NULL DEFAULT '';
            CREATE INDEX observation_grp ON observation (grp);
            CREATE INDEX sign_in_grp ON sign_in (grp);
            """;
        cmd.ExecuteNonQuery();
    }

    // The group an address is counted under: itself for IPv4, its /64 for IPv6.
    private static string Group(string ip)
        => NetworkClassifier.ParseKey(ip) is { } address ? NetworkClassifier.GroupKey(NetworkClassifier.Normalize(address)) : ip;

    // The Seen list asks, for every address, whether it did anything else; without these that is a full scan
    // per row, and a page load slows with the square of the history.
    private static void CreateAddressIndexes(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX observation_ip ON observation (ip);
            CREATE INDEX sign_in_ip ON sign_in (ip);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateBans(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ban (
                ip TEXT NOT NULL PRIMARY KEY,
                country_code TEXT NOT NULL,
                country TEXT NOT NULL,
                failures INTEGER NOT NULL,
                banned INTEGER NOT NULL,
                until INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateSeen(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE seen (
                ip TEXT NOT NULL PRIMARY KEY,
                host TEXT NOT NULL,
                device TEXT NOT NULL,
                country_code TEXT NOT NULL,
                country TEXT NOT NULL,
                requests INTEGER NOT NULL,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL);
            CREATE INDEX seen_last_seen ON seen (last_seen);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CreateBlocked(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE blocked (
                ip TEXT NOT NULL PRIMARY KEY,
                country_code TEXT NOT NULL,
                country TEXT NOT NULL,
                reason TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL);
            CREATE INDEX blocked_last_seen ON blocked (last_seen);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Write-ahead logging is stored in the file, so it is set once, with the schema, rather than per connection.
    private static SchemaAction OpenSchema(SqliteConnection connection)
    {
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        return DatabaseSchema.Open(connection, CurrentVersion, MinReaderVersion, CreateSchema, Migrate, transactional: true);
    }

    private readonly record struct Filter(int Limit, string Place)
    {
        public bool Local => string.Equals(Place, LocalPlace, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        if (_ready)
        {
            return;
        }

        lock (_schemaLock)
        {
            if (_ready)
            {
                return;
            }

            SchemaAction action;
            try
            {
                action = OpenSchema(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                // Not a database at all, or damaged: treated like a failed migration. Any other error, such as
                // a locked or unreadable file, is passed on: the history in it is fine and must not be set aside.
                _logger.LogWarning("People history database is damaged: {Message}", ex.Message);
                action = SchemaAction.MigrationFailed;
            }

            if (action is SchemaAction.TooNew or SchemaAction.MigrationFailed)
            {
                // History is a convenience, not a record anyone depends on: set the file aside and start fresh.
                _logger.LogWarning("People history database could not be used ({Action}); starting a new one", action);
                connection.Close();
                SqliteConnection.ClearAllPools();
                DatabaseSchema.SetAside(_path, action == SchemaAction.TooNew ? "newer" : "corrupt", 2, _logger);
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                if (OpenSchema(connection) is SchemaAction.TooNew or SchemaAction.MigrationFailed)
                {
                    throw new InvalidOperationException("A new People history database could not be created.");
                }
            }

            _ready = true;
        }
    }
}
