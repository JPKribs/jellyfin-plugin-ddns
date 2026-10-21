using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Refuses internet connections from blocked and banned addresses and by country, before TLS starts. Local and trusted
/// networks never reach it. What it refuses is buffered here and written to the People history once a minute,
/// so a scan costs memory for a bounded set of addresses rather than a database write per connection.
/// </summary>
public sealed class BlockFilter : IConnectionFilter
{
    // Enough for a long scan; beyond it, new address groups are counted by country but not listed. Groups, not
    // addresses, so one /64 rotating addresses takes a single slot rather than every one.
    private const int MaxPending = 5000;

    private readonly GeoIpService _geo;
    private readonly ServerLocation _server;
    private readonly BanList _bans;
    private readonly ILogger<BlockFilter> _logger;
    private readonly ConcurrentDictionary<string, int> _refused = new(StringComparer.Ordinal);
    private PendingBuffer _pending = new();
    private long _byAddress;
    private long _byBan;
    private long _byCountry;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockFilter"/> class.
    /// </summary>
    /// <param name="geo">The location lookup.</param>
    /// <param name="server">The country this server is in.</param>
    /// <param name="bans">The addresses banned for failed sign-ins.</param>
    /// <param name="logger">The logger.</param>
    public BlockFilter(GeoIpService geo, ServerLocation server, BanList bans, ILogger<BlockFilter> logger)
    {
        _bans = bans;
        _geo = geo;
        _server = server;
        _logger = logger;
    }

    /// <summary>
    /// Connections the country rules refused since startup, by country code (empty for unknown).
    /// </summary>
    /// <returns>The counts.</returns>
    public Dictionary<string, int> Refused() => _refused.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>Gets the country this server is in; empty fields while unknown.</summary>
    public GeoLocation ServerCountry => _server.Location;

    /// <summary>
    /// Takes how many connections each rule refused since the last call, every one counted, including those
    /// past the history's limits.
    /// </summary>
    /// <returns>The counts.</returns>
    public RefusalTotals TakeTotals()
    {
        var totals = new RefusalTotals(Interlocked.Exchange(ref _byAddress, 0), Interlocked.Exchange(ref _byBan, 0), Interlocked.Exchange(ref _byCountry, 0));
        if (totals.Total > 0)
        {
            _logger.LogDebug("Refused {Count} internet connections in the last minute: {Detail}", totals.Total, totals.Describe());
        }

        return totals;
    }

    /// <summary>
    /// Takes the refusals buffered since the last call.
    /// </summary>
    /// <returns>One entry per address.</returns>
    public List<BlockedAttempt> Drain()
    {
        var taken = Interlocked.Exchange(ref _pending, new PendingBuffer());
        return taken.Attempts.Values.ToList();
    }

    /// <inheritdoc />
    public bool Allow(IPAddress address, ProxyRuntime runtime, bool countries = true, bool record = true)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.BlockedNetworks.Any(n => n.Contains(address)))
        {
            if (record)
            {
                Record(address, BlockReason.Address, _geo.IsLoaded ? _geo.Lookup(address) : null);
            }

            return false;
        }

        if (_bans.IsBanned(address))
        {
            if (record)
            {
                Record(address, BlockReason.Ban, _geo.IsLoaded ? _geo.Lookup(address) : null);
            }

            return false;
        }

        var options = runtime.Options;
        if (!countries || !CountryRules.NeedsDatabase(options))
        {
            return true;
        }

        var loaded = _geo.IsLoaded;
        var location = loaded ? _geo.Lookup(address) : null;
        var country = location?.CountryCode ?? string.Empty;
        if (CountryRules.Allows(options, loaded, country, _server.Location.CountryCode))
        {
            return true;
        }

        if (record)
        {
            _refused.AddOrUpdate(country, 1, (_, n) => n + 1);
            Record(address, BlockReason.Country, location);
        }

        return false;
    }

    private void Record(IPAddress address, BlockReason reason, GeoLocation? location)
    {
        switch (reason)
        {
            case BlockReason.Address:
                Interlocked.Increment(ref _byAddress);
                break;
            case BlockReason.Ban:
                Interlocked.Increment(ref _byBan);
                break;
            default:
                Interlocked.Increment(ref _byCountry);
                break;
        }

        var now = DateTime.UtcNow;
        var buffer = Volatile.Read(ref _pending);
        var pending = buffer.Attempts;
        var key = NetworkClassifier.GroupKey(address);
        if (pending.TryGetValue(key, out var existing))
        {
            lock (existing)
            {
                existing.Attempts++;
                existing.LastUtc = now;
            }
        }
        else if (pending.Count < MaxPending && buffer.TakeNetworkSlot(address))
        {
            pending.TryAdd(key, new BlockedAttempt
            {
                Ip = key,
                CountryCode = location?.CountryCode ?? string.Empty,
                Country = location?.Country ?? string.Empty,
                Reason = reason,
                Attempts = 1,
                FirstUtc = now,
                LastUtc = now
            });
        }

    }

    // One minute's refusals, with how many new groups each IPv6 /48 has taken, swapped out whole on each drain.
    // A banned /48 would otherwise fill every slot and push real refusals out of the history.
    private sealed class PendingBuffer
    {
        private const int PerNetwork = 16;
        private readonly ConcurrentDictionary<string, int> _networks = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, BlockedAttempt> Attempts { get; } = new(StringComparer.Ordinal);

        public bool TakeNetworkSlot(IPAddress address)
        {
            var network = NetworkClassifier.PrefixKey(address, 48);
            return network is null || _networks.AddOrUpdate(network, 1, (_, n) => n + 1) <= PerNetwork;
        }
    }
}

/// <summary>
/// How many connections each rule refused over a period.
/// </summary>
/// <param name="Address">Refused by the block list.</param>
/// <param name="Ban">Refused by a ban for failed sign-ins.</param>
/// <param name="Country">Refused by the country rules.</param>
public readonly record struct RefusalTotals(long Address, long Ban, long Country)
{
    /// <summary>Gets every refusal counted.</summary>
    public long Total => Address + Ban + Country;

    /// <summary>
    /// Adds two periods together.
    /// </summary>
    /// <param name="a">One period.</param>
    /// <param name="b">The other.</param>
    /// <returns>The sum.</returns>
    public static RefusalTotals operator +(RefusalTotals a, RefusalTotals b) => new(a.Address + b.Address, a.Ban + b.Ban, a.Country + b.Country);

    /// <summary>
    /// Adds another period to this one.
    /// </summary>
    /// <param name="other">The other period.</param>
    /// <returns>The sum.</returns>
    public RefusalTotals Add(RefusalTotals other) => this + other;

    /// <summary>
    /// Describes the counts, like <c>12 by country, 3 by a ban</c>.
    /// </summary>
    /// <returns>The description, empty when nothing was refused.</returns>
    public string Describe()
    {
        var parts = new System.Collections.Generic.List<string>(3);
        if (Country > 0)
        {
            parts.Add(Country + " by the country rules");
        }

        if (Ban > 0)
        {
            parts.Add(Ban + " by bans");
        }

        if (Address > 0)
        {
            parts.Add(Address + " by the block list");
        }

        return string.Join(", ", parts);
    }
}
