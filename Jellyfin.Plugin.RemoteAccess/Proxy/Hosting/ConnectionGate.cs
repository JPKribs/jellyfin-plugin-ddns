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
/// Runs on every new connection before TLS: caps open internet connections per address, per /56 and in total,
/// and applies the connection filters. A refused connection is closed without a handshake, so it costs almost
/// nothing. Open internet connections are remembered, so a new ban or rule also closes the ones it now refuses.
/// </summary>
public sealed class ConnectionGate : IDisposable
{
    // Concurrent sign-in requests per address group, and per IPv6 /56 and /48. Requests already in flight when a
    // ban lands would otherwise each get a guess, so a client opening many at once, or spreading them over many
    // /64s, multiplied what one ban allows.
    private const int MaxSignInsPerGroup = 2;
    private const int MaxSignInsPerSite = 4;
    private const int MaxSignInsPerNetwork = 8;

    private static readonly int[] _signInLimits = { MaxSignInsPerGroup, MaxSignInsPerSite, MaxSignInsPerNetwork };
    private readonly ProxyRuntimeHolder _holder;
    private readonly IReadOnlyList<IConnectionFilter> _filters;
    private readonly ConcurrentDictionary<IPAddress, int> _open = new();
    private readonly ConcurrentDictionary<IPAddress, int> _openWide = new();
    private readonly ConcurrentDictionary<IPAddress, int> _openNetwork = new();
    private readonly ConcurrentDictionary<string, int> _signIns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ConnectionContext, LiveConnection> _live = new();
    private int _internet;

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
            // still remembered, so one from a trusted network that is later removed is checked again.
            _live[connection] = new LiveConnection(address, countries);
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

        if (!Allowed(address, runtime, countries) || !TryEnterAll(address, runtime.Options))
        {
            connection.Abort();
            return;
        }

        _live[connection] = new LiveConnection(address, countries);
        try
        {
            await next(connection).ConfigureAwait(false);
        }
        finally
        {
            _live.TryRemove(connection, out _);
            ExitAll(address);
        }
    }

    /// <summary>
    /// Closes every open internet connection the current rules now refuse, such as one from an address that
    /// was just banned or blocked. Streams and WebSockets on it end too.
    /// </summary>
    public void Recheck()
    {
        var runtime = _holder.Current;
        foreach (var pair in _live)
        {
            // Already counted when it opened, so closing it now is not recorded as another refusal.
            if (!runtime.Classifier.IsLocal(pair.Value.Address) && !Allowed(pair.Value.Address, runtime, pair.Value.Countries, record: false))
            {
                if (_live.TryRemove(pair.Key, out _))
                {
                    pair.Key.Abort();
                }
            }
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

    private static IEnumerable<(string Key, int Limit)> SignInTiers(IPAddress address)
        => NetworkClassifier.TierKeys(address).Select((key, i) => (key, _signInLimits[i]));

    /// <inheritdoc />
    public void Dispose()
    {
        _holder.Changed -= OnRuntimeChanged;
    }

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

    // An IPv6 attacker can spread connections over many /64s; a /56 gets a few addresses' worth in all, and a
    // /48 twice that, but never more than a quarter of the whole limit.
    private static int WideLimit(int perAddress) => perAddress <= 0 ? 0 : perAddress * 4;

    private static int NetworkLimit(int perAddress, int total)
    {
        var limit = perAddress <= 0 ? 0 : perAddress * 8;
        var share = total > 0 ? Math.Max(1, total / 4) : 0;
        return limit == 0 ? share : share == 0 ? limit : Math.Min(limit, share);
    }

    private bool Allowed(IPAddress address, ProxyRuntime runtime, bool countries, bool record = true)
        => _filters.All(f => f.Allow(address, runtime, countries, record));

    private bool TryEnterAll(IPAddress address, ProxyOptions options)
    {
        var total = Interlocked.Increment(ref _internet);
        if (options.MaxConnections > 0 && total > options.MaxConnections)
        {
            Interlocked.Decrement(ref _internet);
            return false;
        }

        var group = NetworkClassifier.Group(address);
        if (!TryCount(_open, group, options.MaxConnectionsPerIp))
        {
            Interlocked.Decrement(ref _internet);
            return false;
        }

        var wide = NetworkClassifier.Prefix(address, 56);
        if (wide is not null && !TryCount(_openWide, wide, WideLimit(options.MaxConnectionsPerIp)))
        {
            Release(_open, group);
            Interlocked.Decrement(ref _internet);
            return false;
        }

        var network = NetworkClassifier.Prefix(address, 48);
        if (network is not null && !TryCount(_openNetwork, network, NetworkLimit(options.MaxConnectionsPerIp, options.MaxConnections)))
        {
            Release(_openWide, wide!);
            Release(_open, group);
            Interlocked.Decrement(ref _internet);
            return false;
        }

        return true;
    }

    private void ExitAll(IPAddress address)
    {
        Release(_open, NetworkClassifier.Group(address));
        if (NetworkClassifier.Prefix(address, 56) is { } wide)
        {
            Release(_openWide, wide);
        }

        if (NetworkClassifier.Prefix(address, 48) is { } network)
        {
            Release(_openNetwork, network);
        }

        Interlocked.Decrement(ref _internet);
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => Recheck();

    private readonly record struct LiveConnection(IPAddress Address, bool Countries);
}
