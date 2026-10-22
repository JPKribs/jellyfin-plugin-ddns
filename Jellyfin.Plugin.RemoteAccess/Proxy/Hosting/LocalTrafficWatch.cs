using System;
using System.Collections.Generic;
using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Notices when visitors look local. Something in front of the proxy, such as rootless Docker, a tunnel,
/// another proxy or a router, can hand connections over from one local address. Then bans, the block list and
/// country rules never apply to them, and nothing else would say so. It can hide every visitor, or only some,
/// such as IPv6 visitors arriving through docker-proxy from 172.17.0.1 while IPv4 ones keep their real
/// addresses. The sign, within an hour: many requests for the public names from a single local address, and
/// either none from anywhere else or that one address carrying more than half of them all.
/// </summary>
/// <remarks>
/// Only requests for the public names are counted, so the server's own tools reaching it by localhost or a LAN
/// name never are. Loopback counts like any other local address once it asks for a public name, since a
/// tunnel running on the same machine, such as cloudflared, hands every visitor over from 127.0.0.1 or ::1.
/// </remarks>
public sealed class LocalTrafficWatch
{
    /// <summary>How many requests for the public names an hour must see before it says anything.</summary>
    public const int Threshold = 100;

    // Local addresses counted per hour. A household has a few; beyond this, new ones are not counted apart.
    private const int MaxSources = 256;

    private static readonly TimeSpan _window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Action? _firstSeen;
    private readonly Dictionary<IPAddress, int> _sources = new();
    private DateTime _windowStart = DateTime.MinValue;
    private int _publicLocal;
    private int _publicInternet;
    private IPAddress? _top;
    private int _topCount;
    private bool _carried;
    private bool _suspected;
    private bool _reported;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalTrafficWatch"/> class.
    /// </summary>
    /// <param name="firstSeen">Called once, the first time the sign shows.</param>
    public LocalTrafficWatch(Action? firstSeen = null)
    {
        _firstSeen = firstSeen;
    }

    /// <summary>Gets a value indicating whether visitors look local right now.</summary>
    public bool Suspected
    {
        get
        {
            lock (_lock)
            {
                return _suspected;
            }
        }
    }

    /// <summary>Gets a value indicating whether the sign shows while internet visitors also arrive, so only some look local.</summary>
    public bool Partial
    {
        get
        {
            lock (_lock)
            {
                return _suspected && _publicInternet > 0;
            }
        }
    }

    /// <summary>
    /// Counts one request.
    /// </summary>
    /// <param name="local">Whether the client's address is local.</param>
    /// <param name="publicName">Whether it asked for one of the public names.</param>
    /// <param name="client">The client's normalized address.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>True the first time the sign shows.</returns>
    public bool Observe(bool local, bool publicName, IPAddress? client, DateTime now)
    {
        if (!Count(local, publicName, client, now))
        {
            return false;
        }

        _firstSeen?.Invoke();
        return true;
    }

    private bool Count(bool local, bool publicName, IPAddress? client, DateTime now)
    {
        lock (_lock)
        {
            if (now - _windowStart >= _window)
            {
                // A finished hour that showed the sign keeps it shown until this hour has seen enough to judge,
                // one that did not clears it.
                _carried = _windowStart != DateTime.MinValue && Shows();
                _suspected = _carried;
                _windowStart = now;
                _publicLocal = 0;
                _publicInternet = 0;
                _sources.Clear();
                _top = null;
                _topCount = 0;
            }

            if (!publicName)
            {
                return false;
            }

            if (local)
            {
                _publicLocal++;
                if (client is not null && (_sources.Count < MaxSources || _sources.ContainsKey(client)))
                {
                    var count = _sources.TryGetValue(client, out var seen) ? seen + 1 : 1;
                    _sources[client] = count;
                    if (count > _topCount)
                    {
                        _top = client;
                        _topCount = count;
                    }
                }
            }
            else
            {
                _publicInternet++;
            }

            var shows = Shows();
            if (!shows)
            {
                // Enough requests this hour to judge it, so an earlier hour no longer speaks for it.
                if (_publicLocal + _publicInternet >= Threshold)
                {
                    _carried = false;
                }

                _suspected = _carried;
                return false;
            }

            if (!_suspected)
            {
                _suspected = true;
                if (!_reported)
                {
                    _reported = true;
                    return true;
                }
            }

            return false;
        }
    }

    // Every public request from one local address and none from the internet, or one local address carrying
    // more than half of all of them while some came from the internet too.
    private bool Shows()
    {
        if (_top is null)
        {
            return false;
        }

        var only = _publicInternet == 0 && _sources.Count == 1 && _topCount >= Threshold;
        var most = _topCount > Threshold && _topCount * 2L > _publicLocal + (long)_publicInternet;
        return only || most;
    }
}
