using System;
using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Hosting;

/// <summary>
/// Notices when every visitor looks local. Something in front of the proxy, such as rootless Docker, a tunnel,
/// another proxy or a router, can hand every connection over from one local address. Then bans, the block list
/// and country rules never apply, and nothing else would say so. The sign: many requests for the public names
/// within an hour, none from the internet, all from a single local address.
/// </summary>
public sealed class LocalTrafficWatch
{
    /// <summary>How many requests for the public names an hour must see before it says anything.</summary>
    public const int Threshold = 100;

    private static readonly TimeSpan _window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Action? _firstSeen;
    private DateTime _windowStart = DateTime.MinValue;
    private int _publicLocal;
    private int _internet;
    private IPAddress? _source;
    private bool _manySources;
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

    /// <summary>Gets a value indicating whether every visitor looks local right now.</summary>
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

    /// <summary>
    /// Counts one request.
    /// </summary>
    /// <param name="local">Whether the client's address is local.</param>
    /// <param name="publicName">Whether it asked for one of the public names.</param>
    /// <param name="client">The client's address.</param>
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
                // A finished hour that showed the sign keeps it shown, one that did not clears it.
                _suspected = _windowStart != DateTime.MinValue && Shows();
                _windowStart = now;
                _publicLocal = 0;
                _internet = 0;
                _source = null;
                _manySources = false;
            }

            if (!local)
            {
                _internet++;
                _suspected = false;
                return false;
            }

            if (!publicName)
            {
                return false;
            }

            _publicLocal++;
            if (_source is null)
            {
                _source = client;
            }
            else if (client is not null && !client.Equals(_source))
            {
                _manySources = true;
            }

            if (!_suspected && Shows())
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

    private bool Shows() => _publicLocal >= Threshold && _internet == 0 && !_manySources;
}
