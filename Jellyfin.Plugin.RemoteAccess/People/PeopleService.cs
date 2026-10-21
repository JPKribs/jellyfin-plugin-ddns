using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.DynamicDns.Utilities;

namespace Jellyfin.Plugin.DynamicDns.People;

/// <summary>
/// Turns sign-ins and open sessions into People history: who, from which IP and location, on which device,
/// and through which address. Recording never throws into Jellyfin; a failure is logged and skipped.
/// </summary>
public sealed class PeopleService
{
    private static readonly TimeSpan _failureGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _activeWindow = TimeSpan.FromMinutes(5);

    private readonly PeopleStore _store;
    private readonly GeoIpService _geo;
    private readonly AccessRecorder _access;
    private readonly INetworkManager _network;
    private readonly ISessionManager _sessions;
    private readonly ILogger<PeopleService> _logger;
    private readonly RepeatedProblem _writes = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleService"/> class.
    /// </summary>
    /// <param name="store">The history store.</param>
    /// <param name="geo">The location lookup.</param>
    /// <param name="access">The proxy's record of addresses used.</param>
    /// <param name="network">Jellyfin's network manager, for the LAN test.</param>
    /// <param name="sessions">Jellyfin's session manager.</param>
    /// <param name="logger">The logger.</param>
    public PeopleService(PeopleStore store, GeoIpService geo, AccessRecorder access, INetworkManager network, ISessionManager sessions, ILogger<PeopleService> logger)
    {
        _store = store;
        _geo = geo;
        _access = access;
        _network = network;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Gets the location database detail to keep loaded: the history's choice, and at least country detail
    /// while country rules are on.
    /// </summary>
    public static LocationDetail RequiredLocation
    {
        get
        {
            var rulesOn = Plugin.Instance?.ReadConfiguration(c => ProxyCountryRules.CountryRulesOn(c.Proxy)) ?? false;
            return CountryRules.RequiredDetail(Recording ? Settings.Location : LocationDetail.Off, rulesOn);
        }
    }

    /// <summary>
    /// Gets a value indicating whether history is being recorded: it is on, and so is the proxy. A Dynamic DNS
    /// install that never turns the proxy on records nothing and downloads nothing.
    /// </summary>
    public static bool Recording
        => Plugin.Instance?.ReadConfiguration(c => c.People.Enabled && c.Proxy.Enabled) ?? false;

    /// <summary>Gets the current People settings.</summary>
    public static PeopleSettings Settings => Plugin.Instance?.ReadConfiguration(c => c.People) ?? new PeopleSettings();

    /// <summary>
    /// Parses the address Jellyfin reports for a client, which may carry an IPv4-mapped prefix or a port.
    /// </summary>
    /// <param name="value">The reported address.</param>
    /// <returns>The address, or null when it is not one.</returns>
    public static IPAddress? ParseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (IPAddress.TryParse(text, out var ip))
        {
            return NetworkClassifier.Normalize(ip);
        }

        return IPEndPoint.TryParse(text, out var endPoint) ? NetworkClassifier.Normalize(endPoint.Address) : null;
    }

    /// <summary>
    /// Records a sign-in attempt reported by Jellyfin.
    /// </summary>
    /// <param name="success">Whether it succeeded.</param>
    /// <param name="username">The username.</param>
    /// <param name="userId">The user id, or empty.</param>
    /// <param name="remote">The reported client address.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="deviceName">The device name.</param>
    /// <param name="client">The app name.</param>
    public void RecordSignIn(bool success, string username, string userId, string? remote, string deviceId, string deviceName, string client)
    {
        if (!Recording)
        {
            return;
        }

        var ip = ParseAddress(remote);
        var ipText = ip?.ToString() ?? string.Empty;

        // The proxy saw this failure too; claiming it keeps it from also being logged as an unknown username.
        var url = success ? _access.UrlFor(ipText) : _access.Claim(ipText);
        var location = _geo.Lookup(ip);
        var local = IsLocal(ip, TrustedNetworks());
        Safely(() =>
        {
            _store.RecordAttempt(new SignInAttempt
            {
                TimeUtc = DateTime.UtcNow,
                Username = username,
                UserId = userId,
                Success = success,
                Ip = ipText,
                Host = url,
                Client = client,
                DeviceName = deviceName,
                IsLocal = local,
                CountryCode = location.CountryCode,
                Country = location.Country,
                City = location.City
            });

            if (success && userId.Length > 0)
            {
                _store.RecordObservation(Observe(userId, username, ipText, ip, url, deviceId, deviceName, client, DateTime.UtcNow, location, local));
            }
        });
    }

    /// <summary>
    /// Records every recently active session, and logs proxy-seen failures no Jellyfin event claimed.
    /// </summary>
    public void Poll()
    {
        // The proxy's failures are taken either way, so none pile up while history is off.
        var failures = _access.TakeUnclaimed(_failureGrace);
        if (!Recording)
        {
            return;
        }

        var trusted = TrustedNetworks();
        foreach (var failure in failures)
        {
            var ip = ParseAddress(failure.Ip);
            var location = _geo.Lookup(ip);
            Safely(() => _store.RecordAttempt(new SignInAttempt
            {
                TimeUtc = failure.At,
                Success = false,
                Ip = failure.Ip,
                Host = failure.Url,
                IsLocal = IsLocal(ip, trusted),
                CountryCode = location.CountryCode,
                Country = location.Country,
                City = location.City
            }));
        }

        var cutoff = DateTime.UtcNow - _activeWindow;
        foreach (var session in _sessions.Sessions.Where(s => !s.UserId.Equals(Guid.Empty) && s.LastActivityDate >= cutoff).ToList())
        {
            var ip = ParseAddress(session.RemoteEndPoint);
            var ipText = ip?.ToString() ?? string.Empty;
            var location = _geo.Lookup(ip);
            var observation = Observe(
                session.UserId.ToString("N"),
                session.UserName ?? string.Empty,
                ipText,
                ip,
                _access.UrlFor(ipText),
                session.DeviceId ?? string.Empty,
                session.DeviceName ?? string.Empty,
                session.Client ?? string.Empty,
                session.LastActivityDate,
                location,
                IsLocal(ip, trusted));
            Safely(() => _store.RecordObservation(observation));
        }
    }

    /// <summary>
    /// Removes history older than the configured window.
    /// </summary>
    public void Prune() => Safely(() => _store.Prune(Settings.HistoryDays));

    private static Observation Observe(string userId, string userName, string ipText, IPAddress? ip, string url, string deviceId, string deviceName, string client, DateTime seen, GeoLocation location, bool local)
        => new()
        {
            UserId = userId,
            UserName = userName,
            Ip = ipText,
            Host = url,
            DeviceId = deviceId,
            DeviceName = deviceName,
            Client = client,
            IsLocal = local,
            CountryCode = location.CountryCode,
            Country = location.Country,
            City = location.City,
            FirstSeenUtc = seen.ToUniversalTime(),
            LastSeenUtc = seen.ToUniversalTime()
        };

    // Local means what it means to the proxy: loopback, the LAN, or a trusted network.
    private static List<System.Net.IPNetwork> TrustedNetworks()
        => NetworkClassifier.ParseNetworks(Plugin.Instance?.ReadConfiguration(c => c.Proxy.Advanced.TrustedNetworks.ToList()));

    private bool IsLocal(IPAddress? ip, IEnumerable<System.Net.IPNetwork> trusted)
    {
        if (ip is null)
        {
            return false;
        }

        ip = NetworkClassifier.Normalize(ip);
        return IPAddress.IsLoopback(ip) || _network.IsInLocalNetwork(ip) || trusted.Any(n => n.Contains(ip));
    }

    private void Safely(Action write)
    {
        try
        {
            write();
            _writes.Succeeded(() => _logger.LogInformation("People history is being recorded again"));
        }
        catch (Exception ex) when (ex is SqliteException or System.IO.IOException or InvalidOperationException)
        {
            // Every request writes, so a broken database warns once rather than on each one.
            _writes.Failed(level => _logger.Log(level, "Could not record People history: {Message}", ex.Message));
        }
    }
}
