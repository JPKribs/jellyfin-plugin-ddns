using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Runs on every new connection before TLS: caps open internet connections per address, per /56 and /48 and in
/// total, and applies the connection filters. A refused connection is closed without a handshake, so it costs
/// almost nothing. Open internet connections are remembered, so a new ban or rule also closes the ones it now
/// refuses. It also caps internet requests in flight per address group, which HTTP/2 would otherwise multiply.
/// </summary>
public sealed class ConnectionGate : IDisposable
{
    // Concurrent sign-in requests per address group, and per IPv6 /56 and /48. Requests already in flight when a
    // ban lands would otherwise each get a guess, so a client opening many at once, or spreading them over many
    // /64s, multiplied what one ban allows.
    private const int MaxSignInsPerGroup = 2;
    private const int MaxSignInsPerSite = 4;
    private const int MaxSignInsPerNetwork = 8;

    // Requests in flight per address group, /56 and /48, as a multiple of each one's connection cap. Generous, so
    // a household streaming over HTTP/2 never meets it, while one address can no longer hold its connection cap
    // times the streams each connection allows.
    private const int RequestsPerConnection = 4;

    private static readonly int[] _signInLimits = { MaxSignInsPerGroup, MaxSignInsPerSite, MaxSignInsPerNetwork };
    private readonly ProxyRuntimeHolder _holder;
    private readonly IReadOnlyList<IConnectionFilter> _filters;
    private readonly ConcurrentDictionary<IPAddress, int> _open = new();
    private readonly ConcurrentDictionary<IPAddress, int> _openWide = new();
    private readonly ConcurrentDictionary<IPAddress, int> _openNetwork = new();
    private readonly ConcurrentDictionary<IPAddress, int> _requests = new();
    private readonly ConcurrentDictionary<IPAddress, int> _requestsWide = new();
    private readonly ConcurrentDictionary<IPAddress, int> _requestsNetwork = new();
    private readonly ConcurrentDictionary<string, int> _signIns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ConnectionContext, LiveConnection> _live = new();
    private int _internet;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionGate"/> class.
    /// </summary>
    /// <param name="holder">The runtime holder.</param>
    /// <param name="filters">The connection filters.</param>
    public ConnectionGate(ProxyRuntimeHolder holder, IEnumerable<IConnectionFilter> filters)
    {
        _holder = holder;
        _filters = filters.ToList();
        _holder.Changed += OnRuntimeChanged;

        // Built before the proxy listens, so saved bans and the like hold from the first connection.
        foreach (var filter in _filters)
        {
            filter.Prepare();
        }
    }

    /// <summary>Gets the filters, which the request policy also applies to every request.</summary>
    public IReadOnlyList<IConnectionFilter> Filters => _filters;

    /// <summary>Gets the number of addresses with open internet connections.</summary>
    public int TrackedAddresses => _open.Count;

    /// <summary>Gets the number of open internet connections.</summary>
    public int InternetConnections => Volatile.Read(ref _internet);

    /// <summary>
    /// Kestrel connection middleware entry point.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="next">The rest of the connection pipeline (TLS, then HTTP).</param>
    /// <param name="countries">
    /// Whether country rules apply. Plain HTTP skips them, since Let's Encrypt validates over port 80 from many
    /// countries; bans and the block list apply on every port.
    /// </param>
    /// <returns>A task that completes when the connection closes.</returns>
    public async Task OnConnectionAsync(ConnectionContext connection, ConnectionDelegate next, bool countries = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(next);

        var runtime = _holder.Current;
        var address = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        if (address is null)
        {
            await next(connection).ConfigureAwait(false);
            return;
        }

        address = NetworkClassifier.Normalize(address);
        if (runtime.Classifier.IsLocal(address))
        {
            // Local clients are never counted, so the internet cannot fill the limit and lock them out. They are
            // still remembered, so one from a trusted network that is later removed is closed and checked again.
            _live[connection] = new LiveConnection(address, null, countries, Interlocked.Increment(ref _sequence));
            try
            {
                await next(connection).ConfigureAwait(false);
            }
            finally
            {
                _live.TryRemove(connection, out _);
            }

            return;
        }

        if (!Allowed(address, runtime, countries))
        {
            connection.Abort();
            return;
        }

        var keys = ClientKeys.For(address);
        if (!TryEnterAll(keys, runtime.Options))
        {
            foreach (var filter in _filters)
            {
                filter.RecordLimited(address);
            }

            connection.Abort();
            return;
        }

        // Kept on the connection, so each request on it is counted without working the keys out again.
        connection.Features.Set(keys);
        _live[connection] = new LiveConnection(address, keys, countries, Interlocked.Increment(ref _sequence));
        try
        {
            await next(connection).ConfigureAwait(false);
        }
        finally
        {
            _live.TryRemove(connection, out _);
            ExitAll(keys);
        }
    }

    /// <summary>
    /// Closes every open internet connection the current rules now refuse, such as one from an address that
    /// was just banned or blocked. Streams and WebSockets on it end too. A connection that opened as local but
    /// no longer is, such as one from a trusted network just removed, is closed too.
    /// </summary>
    public void Recheck()
    {
        var runtime = _holder.Current;

        // One that opened as local was never counted or filtered, so it is closed and its client comes back
        // through the gate. An internet one is closed when the rules now refuse it. It was counted when it
        // opened, so closing it now is not recorded as another refusal.
        var refused = _live.Where(p => !runtime.Classifier.IsLocal(p.Value.Address)
            && (p.Value.Local || !Allowed(p.Value.Address, runtime, p.Value.Countries, record: false)));
        // Only the call that removes a connection aborts it, so one closing on its own meanwhile is left alone.
        foreach (var pair in refused.Where(p => _live.TryRemove(p.Key, out _)))
        {
            pair.Key.Abort();
        }
    }

    /// <summary>
    /// Takes one of an address group's sign-in slots.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <returns>True when a slot was free; it must then be released with <see cref="ExitSignIn"/>.</returns>
    public bool TryEnterSignIn(IPAddress address)
    {
        var taken = new List<string>(3);
        foreach (var (key, limit) in SignInTiers(address))
        {
            if (!TryCount(_signIns, key, limit))
            {
                foreach (var held in taken)
                {
                    Release(_signIns, held);
                }

                return false;
            }

            taken.Add(key);
        }

        return true;
    }

    /// <summary>
    /// Releases a sign-in slot taken by <see cref="TryEnterSignIn"/>.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    public void ExitSignIn(IPAddress address)
    {
        foreach (var (key, _) in SignInTiers(address))
        {
            Release(_signIns, key);
        }
    }

    /// <summary>
    /// Takes a request slot for an internet client, under its address group and, for IPv6, its /56 and /48.
    /// </summary>
    /// <param name="address">The normalized address.</param>
    /// <param name="keys">The keys the gate kept on the connection, or null to work them out.</param>
    /// <param name="options">The options in force.</param>
    /// <returns>The keys to release with <see cref="ExitRequest"/>, or null when the client has too many in flight.</returns>
    internal ClientKeys? TryEnterRequest(IPAddress address, ClientKeys? keys, ProxyOptions options)
    {
        keys ??= ClientKeys.For(address);
        var perAddress = options.MaxConnectionsPerIp;
        if (!TryCount(_requests, keys.Group, RequestLimit(perAddress)))
        {
            return null;
        }

        if (keys.Wide is { } wide && !TryCount(_requestsWide, wide, RequestLimit(WideLimit(perAddress, options.MaxConnections))))
        {
            Release(_requests, keys.Group);
            return null;
        }

        if (keys.Network is { } network && !TryCount(_requestsNetwork, network, RequestLimit(NetworkLimit(perAddress, options.MaxConnections))))
        {
            Release(_requestsWide, keys.Wide!);
            Release(_requests, keys.Group);
            return null;
        }

        return keys;
    }

    /// <summary>
    /// Releases a request slot taken by <see cref="TryEnterRequest"/>.
    /// </summary>
    /// <param name="keys">The keys it returned.</param>
    internal void ExitRequest(ClientKeys keys)
    {
        Release(_requests, keys.Group);
        if (keys.Wide is { } wide)
        {
            Release(_requestsWide, wide);
        }

        if (keys.Network is { } network)
        {
            Release(_requestsNetwork, network);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _holder.Changed -= OnRuntimeChanged;
    }

    // An IPv6 attacker can spread connections over many /64s; a /56 gets a few addresses' worth in all, and a
    // /48 twice that. Each also gets only a small share of the whole limit, a 64th for a /56 and a 32nd for a
    // /48, so a few networks cannot fill it. The share is never below one address's worth.
    internal static int WideLimit(int perAddress, int total) => Capped(perAddress, 4, total, 64);

    internal static int NetworkLimit(int perAddress, int total) => Capped(perAddress, 8, total, 32);

    private static int Capped(int perAddress, int factor, int total, int divisor)
    {
        var limit = perAddress <= 0 ? 0 : perAddress * factor;
        var share = total > 0 ? Math.Max(Math.Max(1, perAddress), total / divisor) : 0;
        return limit == 0 ? share : share == 0 ? limit : Math.Min(limit, share);
    }

    private static int RequestLimit(int connections) => connections <= 0 ? 0 : connections * RequestsPerConnection;

    private static IEnumerable<(string Key, int Limit)> SignInTiers(IPAddress address)
        => NetworkClassifier.TierKeys(address).Select((key, i) => (key, _signInLimits[i]));

    private static bool TryCount<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key, int limit)
        where TKey : notnull
    {
        var count = counts.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (limit <= 0 || count <= limit)
        {
            return true;
        }

        Release(counts, key);
        return false;
    }

    private static void Release<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        while (counts.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (counts.TryRemove(new KeyValuePair<TKey, int>(key, count)))
                {
                    return;
                }
            }
            else if (counts.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    private bool Allowed(IPAddress address, ProxyRuntime runtime, bool countries, bool record = true)
        => _filters.All(f => f.Allow(address, runtime, countries, record));

    private bool TryEnterAll(ClientKeys keys, ProxyOptions options)
    {
        var perAddress = options.MaxConnectionsPerIp;
        if (!TryCount(_open, keys.Group, perAddress))
        {
            return false;
        }

        if (keys.Wide is { } wide && !TryCount(_openWide, wide, WideLimit(perAddress, options.MaxConnections)))
        {
            Release(_open, keys.Group);
            return false;
        }

        if (keys.Network is { } network && !TryCount(_openNetwork, network, NetworkLimit(perAddress, options.MaxConnections)))
        {
            Release(_openWide, keys.Wide!);
            Release(_open, keys.Group);
            return false;
        }

        var total = Interlocked.Increment(ref _internet);
        if (options.MaxConnections > 0 && total > options.MaxConnections && !MakeRoom(keys))
        {
            Interlocked.Decrement(ref _internet);
            ReleaseTiers(keys);
            return false;
        }

        return true;
    }

    // The whole limit is full. Idle keep-alive connections from a few address blocks would otherwise lock every
    // other visitor out, so the block holding the most connections gives up its oldest one to a newcomer from
    // elsewhere. Only a block holding more than the newcomer's own does, so equals never take turns closing each
    // other's. The closed connection's count drops as it ends, so the total is one over until then.
    private bool MakeRoom(ClientKeys keys)
    {
        IPAddress? block = null;
        var tier = 0;
        var most = 0;
        void Heaviest(ConcurrentDictionary<IPAddress, int> counts, IPAddress? own, int level)
        {
            var mine = own is not null && counts.TryGetValue(own, out var held) ? held : 1;
            // The filter runs as the loop goes, so it compares against the heaviest found so far.
            foreach (var pair in counts.Where(p => p.Value > mine && p.Value > most && !p.Key.Equals(own)))
            {
                block = pair.Key;
                tier = level;
                most = pair.Value;
            }
        }

        Heaviest(_open, keys.Group, 0);
        Heaviest(_openWide, keys.Wide, 1);
        Heaviest(_openNetwork, keys.Network, 2);
        if (block is null)
        {
            return false;
        }

        ConnectionContext? oldest = null;
        var first = long.MaxValue;
        foreach (var pair in _live.Where(p => p.Value.Keys is { } held && p.Value.Sequence < first && block.Equals(held.Tier(tier))))
        {
            oldest = pair.Key;
            first = pair.Value.Sequence;
        }

        // Only the call that removes a connection aborts it, as in Recheck.
        if (oldest is null || !_live.TryRemove(oldest, out _))
        {
            return false;
        }

        oldest.Abort();
        return true;
    }

    private void ReleaseTiers(ClientKeys keys)
    {
        Release(_open, keys.Group);
        if (keys.Wide is { } wide)
        {
            Release(_openWide, wide);
        }

        if (keys.Network is { } network)
        {
            Release(_openNetwork, network);
        }
    }

    private void ExitAll(ClientKeys keys)
    {
        ReleaseTiers(keys);
        Interlocked.Decrement(ref _internet);
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => Recheck();

    /// <summary>
    /// The keys an internet client is counted under: its address group, and for IPv6 its /56 and /48. Worked
    /// out once per connection and kept on it.
    /// </summary>
    /// <param name="Group">The address group, an IPv4 address or an IPv6 /64.</param>
    /// <param name="Wide">The IPv6 /56, null for IPv4.</param>
    /// <param name="Network">The IPv6 /48, null for IPv4.</param>
    internal sealed record ClientKeys(IPAddress Group, IPAddress? Wide, IPAddress? Network)
    {
        /// <summary>
        /// Works out the keys for an address.
        /// </summary>
        /// <param name="address">The normalized address.</param>
        /// <returns>The keys.</returns>
        public static ClientKeys For(IPAddress address)
            => new(NetworkClassifier.Group(address), NetworkClassifier.Prefix(address, 56), NetworkClassifier.Prefix(address, 48));

        /// <summary>
        /// The key at one tier.
        /// </summary>
        /// <param name="tier">0 for the group, 1 for the /56, 2 for the /48.</param>
        /// <returns>The key, null for a tier IPv4 has not.</returns>
        public IPAddress? Tier(int tier) => tier switch
        {
            0 => Group,
            1 => Wide,
            _ => Network
        };
    }

    // Keys is null for a connection that opened as local, which is not counted.
    private readonly record struct LiveConnection(IPAddress Address, ClientKeys? Keys, bool Countries, long Sequence)
    {
        public bool Local => Keys is null;
    }
}
