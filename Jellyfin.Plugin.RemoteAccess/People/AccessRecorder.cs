using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Remembers the address each client IP last used to reach Jellyfin, buffers internet visits for the history,
/// and holds sign-in failures the proxy saw until Jellyfin's own failure event claims them. A failure no event
/// claims was for a username that does not exist, which Jellyfin does not report.
/// </summary>
public sealed class AccessRecorder : IAccessRecorder
{
    private const int MaxClients = 10_000;

    // New internet address groups taken into the history per minute, and per IPv6 /48 within that; known ones
    // keep counting past it. Groups, not addresses, so one /64 rotating addresses takes a single slot.
    private const int MaxNewSeen = 1_000;
    private const int MaxNewSeenPerNetwork = 16;
    private static readonly TimeSpan _urlLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan _pairWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (string Url, DateTime At)> _latest = new(StringComparer.Ordinal);
    private readonly List<PendingFailure> _failures = new();
    private readonly List<PendingFailure> _reported = new();
    private readonly object _failureLock = new();
    private SeenBuffer _seen = new();

    /// <inheritdoc />
    public void RecordRequest(IPAddress client, string url, string userAgent, bool local)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = DateTime.UtcNow;
        var key = NetworkClassifier.GroupKey(client);
        if (!_latest.ContainsKey(key) && _latest.Count >= MaxClients)
        {
            MakeRoom(now);
        }

        _latest[key] = (url, now);
        if (local)
        {
            return;
        }

        // Every request of a page load lands here, so a group already buffered only has its counters moved.
        var buffer = Volatile.Read(ref _seen);
        if (buffer.Visits.TryGetValue(key, out var visit))
        {
            lock (visit)
            {
                visit.Requests++;
                visit.LastUtc = now;
                visit.Host = url;
            }
        }
        else if (buffer.Visits.Count < MaxNewSeen && buffer.TakeNetworkSlot(client))
        {
            // Kept as the group, so one /64 rotating addresses is one row in the history, not one per address.
            buffer.Visits.TryAdd(key, new SeenVisit { Ip = key, Host = url, Device = UserAgents.Describe(userAgent), Requests = 1, FirstUtc = now, LastUtc = now });
        }
    }

    // Full: forget addresses older than an hour, then the one used longest ago. Clearing everything would let
    // one client wipe what every other client used.
    private void MakeRoom(DateTime now)
    {
        foreach (var stale in _latest.Where(p => now - p.Value.At >= _urlLifetime).Select(p => p.Key).ToList())
        {
            _latest.TryRemove(stale, out _);
        }

        // Still full: drop the tenth used longest ago in one pass, so the sort is paid once per thousand new
        // clients rather than on every request.
        if (_latest.Count >= MaxClients)
        {
            foreach (var oldest in _latest.OrderBy(p => p.Value.At).Take(MaxClients / 10).Select(p => p.Key).ToList())
            {
                _latest.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>
    /// Takes the internet visits buffered since the last call.
    /// </summary>
    /// <returns>One entry per address.</returns>
    public List<SeenVisit> DrainSeen()
        => Interlocked.Exchange(ref _seen, new SeenBuffer()).Visits.Values.ToList();

    /// <summary>
    /// Forgets everything buffered for the history, so a cleared history does not refill from memory.
    /// </summary>
    public void ClearBuffered()
    {
        DrainSeen();
        lock (_failureLock)
        {
            _failures.Clear();
            _reported.Clear();
        }
    }

    /// <inheritdoc />
    public void RecordSignInFailure(IPAddress client, string url)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_failureLock)
        {
            if (_failures.Count < MaxClients)
            {
                _failures.Add(new PendingFailure(client.ToString(), url, DateTime.UtcNow));
            }
        }
    }

    /// <summary>
    /// The address a client used most recently, if within the last hour.
    /// </summary>
    /// <param name="ip">The client IP.</param>
    /// <returns>The address, or empty.</returns>
    public string UrlFor(string ip)
        => IPAddress.TryParse(ip, out var parsed)
            && _latest.TryGetValue(NetworkClassifier.GroupKey(NetworkClassifier.Normalize(parsed)), out var entry)
            && DateTime.UtcNow - entry.At < _urlLifetime
                ? entry.Url
                : string.Empty;

    /// <summary>
    /// Notes a failed sign-in Jellyfin reported with a username, so the proxy's copy of the same failure is
    /// not logged again as an unknown username. Jellyfin reports before the proxy sees the response, so the
    /// two are paired whenever the proxy's copy is collected.
    /// </summary>
    /// <param name="ip">The client IP.</param>
    /// <returns>The address the client used, or empty.</returns>
    public string Claim(string ip)
    {
        lock (_failureLock)
        {
            if (_reported.Count < MaxClients)
            {
                _reported.Add(new PendingFailure(ip, string.Empty, DateTime.UtcNow));
            }
        }

        return UrlFor(ip);
    }

    /// <summary>
    /// Takes the proxy's failures that waited out the grace period and match no failure Jellyfin reported;
    /// those were for usernames that do not exist.
    /// </summary>
    /// <param name="grace">How long a failure waits for its pair.</param>
    /// <returns>The unpaired failures.</returns>
    public List<PendingFailure> TakeUnclaimed(TimeSpan grace)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - grace;
        lock (_failureLock)
        {
            var ready = _failures.Where(f => f.At <= cutoff).ToList();
            _failures.RemoveAll(f => f.At <= cutoff);
            var unpaired = new List<PendingFailure>();
            if (ready.Count > 0)
            {
                // Grouped by address once, so a burst of failures is paired in one pass rather than each one
                // scanning every reported failure.
                var byIp = new Dictionary<string, List<PendingFailure>>(StringComparer.Ordinal);
                foreach (var reported in _reported)
                {
                    if (!byIp.TryGetValue(reported.Ip, out var list))
                    {
                        byIp[reported.Ip] = list = new List<PendingFailure>(1);
                    }

                    list.Add(reported);
                }

                foreach (var failure in ready)
                {
                    var pair = byIp.TryGetValue(failure.Ip, out var candidates)
                        ? candidates.FindIndex(r => (r.At - failure.At).Duration() <= _pairWindow)
                        : -1;
                    if (pair >= 0)
                    {
                        candidates!.RemoveAt(pair);
                    }
                    else
                    {
                        unpaired.Add(failure);
                    }
                }

                _reported.Clear();
                foreach (var list in byIp.Values)
                {
                    _reported.AddRange(list);
                }
            }

            _reported.RemoveAll(r => now - r.At > _pairWindow + grace);
            return unpaired;
        }
    }

    // One minute's visits, with how many new groups each IPv6 /48 has taken, swapped out whole on each drain.
    private sealed class SeenBuffer
    {
        private readonly ConcurrentDictionary<string, int> _networks = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, SeenVisit> Visits { get; } = new(StringComparer.Ordinal);

        public bool TakeNetworkSlot(IPAddress client)
        {
            var network = NetworkClassifier.PrefixKey(client, 48);
            return network is null || _networks.AddOrUpdate(network, 1, (_, n) => n + 1) <= MaxNewSeenPerNetwork;
        }
    }
}
