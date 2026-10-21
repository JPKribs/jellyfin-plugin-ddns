using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Owns the running proxy: starts it from the saved settings, applies changes live when it can and
/// rebuilds the host when it must, and reports status. All lifecycle changes are serialized.
/// </summary>
public sealed class ProxyManager : IAsyncDisposable
{
    private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(5);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProxyManager> _logger;
    private readonly ProxyPaths _paths;
    private readonly INetworkManager _network;
    private readonly CertificateStore _certificates;
    private readonly Http01ChallengeStore _challenges;
    private readonly People.AccessRecorder _access;
    private readonly People.BlockFilter _blocks;
    private readonly People.BanList _bans;
    private readonly People.GeoIpService _geo;
    private readonly Acme.AcmeCertificateService _acme;
    private readonly JellyfinNetworkView _jellyfin;
    private readonly IConfigurationManager _configuration;
    private readonly Timer _acmeRetry;
    private readonly string _systemId;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ProxyHost? _host;
    private ProxyRuntimeHolder? _holder;
    private DateTime? _startedUtc;
    private string _error = string.Empty;

    // The start failure last written to the activity log, so a proxy that keeps failing the same way is one
    // entry, and its recovery another.
    private string _reportedError = string.Empty;
    private readonly ActivityLogger? _activity;
    private readonly LocalTrafficWatch _localTraffic;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyManager"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory, shared with the proxy host.</param>
    /// <param name="paths">The proxy data paths.</param>
    /// <param name="configuration">Jellyfin's configuration manager.</param>
    /// <param name="network">Jellyfin's network manager, for the LAN test.</param>
    /// <param name="application">The server host, for the ports Jellyfin is using and its id.</param>
    /// <param name="certificates">The certificate store.</param>
    /// <param name="challenges">Pending HTTP-01 challenges.</param>
    /// <param name="access">Where the proxy reports the address each client used.</param>
    /// <param name="blocks">The block list and country rule filter.</param>
    /// <param name="bans">The addresses banned for failed sign-ins.</param>
    /// <param name="geo">The location lookup, loaded when country rules need it.</param>
    /// <param name="acme">Gets and renews certificates from Let's Encrypt.</param>
    /// <param name="activity">The activity log writer, for start failures and recovery.</param>
    public ProxyManager(
        ILoggerFactory loggerFactory,
        ProxyPaths paths,
        IConfigurationManager configuration,
        INetworkManager network,
        IServerApplicationHost application,
        CertificateStore certificates,
        Http01ChallengeStore challenges,
        People.AccessRecorder access,
        People.BlockFilter blocks,
        People.BanList bans,
        People.GeoIpService geo,
        Acme.AcmeCertificateService acme,
        ActivityLogger? activity = null)
    {
        _activity = activity;
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(application);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ProxyManager>();
        _paths = paths;
        _network = network;
        _certificates = certificates;
        _challenges = challenges;
        _access = access;
        _blocks = blocks;
        _bans = bans;
        _localTraffic = new LocalTrafficWatch(ReportEveryoneLooksLocal);

        // One timer for the next Let's Encrypt try, armed and disarmed as the state changes.
        _acmeRetry = new Timer(_ => RetryAcme(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _geo = geo;
        _acme = acme;
        _systemId = application.SystemId;
        _certificates.AvailabilityChanged += OnCertificateAvailabilityChanged;
        _configuration = configuration;
        _jellyfin = JellyfinNetworkView.From(
            configuration.GetNetworkConfiguration(),
            application.HttpPort,
            application.ListenWithHttps ? application.HttpsPort : 0);
    }

    /// <summary>Gets the root devices install to trust the self-signed certificate; null in any other mode.</summary>
    public RootCertificate? SelfSignedRoot => ReadSettings().Certificate.Mode == CertificateMode.SelfSigned ? _certificates.Root : null;

    /// <summary>Gets Jellyfin's running network setup.</summary>
    public JellyfinNetworkView Jellyfin => _jellyfin;

    /// <summary>Gets a value indicating whether the proxy is listening.</summary>
    public bool IsRunning => Volatile.Read(ref _host) is not null;

    /// <summary>
    /// Reads the saved settings and brings the proxy in line: stops it when disabled, updates it in place when
    /// only live settings changed, rebuilds it when listeners changed. Never throws for a bind failure; the
    /// error is kept for the status page.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task.</returns>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        // Once Jellyfin is shutting down, nothing may bind the ports again: the next instance needs them.
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await ApplyCoreAsync(cancellationToken).ConfigureAwait(false);

        // Let's Encrypt checks the address through the running proxy, so a certificate is asked for only once
        // it listens. The planner makes this a no-op while the certificate is current or after a recent failure.
        if (IsRunning && ReadSettings() is { Certificate: { Mode: CertificateMode.Automatic, AcceptTerms: true } } automatic)
        {
            _ = Task.Run(() => EnsureAutomaticCertificateAsync(automatic, _shutdown.Token), _shutdown.Token);
        }
    }

    /// <summary>
    /// Stops the proxy for good as Jellyfin shuts down: no certificate change, retry or settings save may start it
    /// again, and a certificate request still running is cancelled.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task ShutdownAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _certificates.AvailabilityChanged -= OnCertificateAvailabilityChanged;
        await _acmeRetry.DisposeAsync().ConfigureAwait(false);

        await StopAsync().ConfigureAwait(false);
    }

    private async Task ApplyCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            var settings = ReadSettings();
            if (!settings.Enabled || File.Exists(_paths.DisableFlag))
            {
                await StopHostAsync().ConfigureAwait(false);
                _error = settings.Enabled ? "The recovery flag file is present, so the proxy is off." : string.Empty;
                return;
            }

            var runtime = BuildRuntime(settings);

            // Country rules need the location database even when the People page does not.
            if (People.CountryRules.NeedsDatabase(runtime.Options))
            {
                _ = Task.Run(() => _geo.EnsureAsync(People.PeopleService.RequiredLocation, CancellationToken.None), CancellationToken.None);
            }

            // The proxy starts before Jellyfin's own server binds its ports. Taking one of them first would stop
            // Jellyfin from starting, so a clash is refused here rather than left to whichever binds first.
            var clash = JellyfinPortClash(runtime.Listener);
            if (clash.Count > 0)
            {
                await StopHostAsync().ConfigureAwait(false);
                _error = $"Jellyfin itself uses port {string.Join(" and ", clash)}, which the proxy needs. Change Jellyfin's ports under Dashboard → Networking.";
                return;
            }

            if (_host is not null && _holder is not null && _host.Listener == runtime.Listener)
            {
                _holder.Current = runtime;
                _host.Update(runtime);
                return;
            }

            await StopHostAsync().ConfigureAwait(false);
            await StartHostAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the proxy and releases its ports.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopHostAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Starts a throwaway copy of the proxy on a free loopback port and fetches Jellyfin's public system
    /// info through it, proving the proxy can reach this Jellyfin before anything about Jellyfin changes.
    /// </summary>
    /// <param name="settings">The settings to test.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>Null when the probe passed, otherwise what went wrong.</returns>
    public async Task<string?> ProbeAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var runtime = ProxyRuntimeFactory.Build(settings, _jellyfin, IsLan, httpsAvailable: false, probe: true);
        ProxyHost? probe = null;
        try
        {
            probe = await ProxyHost.StartAsync(HostOptions(new ProxyRuntimeHolder(runtime)), cancellationToken).ConfigureAwait(false);
            var address = probe.Addresses.Count > 0 ? probe.Addresses[0] : throw new IOException("The probe did not report an address.");
            return await CheckJellyfinAsync(new Uri(address), runtime.BaseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return "The proxy could not start a test listener: " + ex.Message;
        }
        finally
        {
            if (probe is not null)
            {
                await probe.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await probe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fetches Jellyfin's public system info through the running proxy's HTTP listener.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Null when Jellyfin answered through the proxy, otherwise what went wrong.</returns>
    public async Task<string?> CheckRunningAsync(CancellationToken cancellationToken)
    {
        var host = Volatile.Read(ref _host);
        if (host is null)
        {
            return string.IsNullOrEmpty(_error) ? "The proxy is not running." : _error;
        }

        var address = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), host.Listener.HttpPort).Uri;
        return await CheckJellyfinAsync(address, _jellyfin.BaseUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads the certificate from its files, or replaces a self-signed one that is close to expiring, and
    /// brings the proxy in line. Run by the scheduled certificate task; saving settings does the same.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The certificate status afterwards.</returns>
    public async Task<CertificateStatus> RefreshCertificateAsync(CancellationToken cancellationToken)
    {
        await ApplyCoreAsync(cancellationToken).ConfigureAwait(false);
        var settings = ReadSettings();
        if (IsRunning && settings.Certificate is { Mode: CertificateMode.Automatic, AcceptTerms: true })
        {
            await EnsureAutomaticCertificateAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        _certificates.Refresh();
        return _certificates.Status;
    }

    /// <summary>
    /// Orders a new Let's Encrypt certificate now, past the usual timing rules, for an administrator who asked.
    /// </summary>
    /// <param name="cancellationToken">Cancels the order.</param>
    /// <returns>Whether a certificate was issued, and what to tell the administrator.</returns>
    public async Task<(bool Ok, string Message)> RequestCertificateAsync(CancellationToken cancellationToken)
    {
        var settings = ReadSettings();
        if (settings.Certificate is not { Mode: CertificateMode.Automatic, AcceptTerms: true })
        {
            return (false, "Choose Let's Encrypt and accept its terms first.");
        }

        if (!IsRunning)
        {
            return (false, "The proxy is not running, so Let's Encrypt cannot check your address.");
        }

        var records = ReadRecords();
        var hostnames = CertificateHostnames(settings);
        if (await _acme.EnsureAsync(hostnames, records, _certificates.Status, cancellationToken, force: true).ConfigureAwait(false))
        {
            _certificates.Refresh();
            return (true, "Let's Encrypt issued a new certificate.");
        }

        var acme = _acme.Describe(hostnames, records);
        return (false, acme.Issuing
            ? "A certificate is already being requested."
            : acme.Names.Count == 0
                ? "Let's Encrypt needs an address that is not a wildcard."
                : acme.LastError.Length > 0 ? acme.LastError : "Let's Encrypt did not issue a certificate.");
    }

    // A new certificate on disk is loaded at once; the store then starts the HTTPS listener if it was off.
    private async Task EnsureAutomaticCertificateAsync(ProxySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (await _acme.EnsureAsync(CertificateHostnames(settings), ReadRecords(), _certificates.Status, cancellationToken).ConfigureAwait(false))
            {
                _certificates.Refresh();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Run in the background, so nothing else would ever see this.
            _logger.LogWarning(ex, "Getting a certificate from Let's Encrypt failed");
        }

        ScheduleAcmeRetry(settings);
    }

    // After a failure the planner waits out a backoff. Nothing else would ask again at that time, so a timer does.
    private void ScheduleAcmeRetry(ProxySettings settings)
    {
        // Shutdown has disposed the timer for good; one armed now would never be disposed.
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        var next = _acme.Describe(CertificateHostnames(settings), ReadRecords()).NextAttemptUtc;
        if (next is not { } when)
        {
            ArmAcmeRetry(Timeout.InfiniteTimeSpan);
            return;
        }

        // At most a day ahead: a timer cannot wait past about 49 days, and the planner decides again when it fires.
        var due = when - DateTime.UtcNow;
        due = due < TimeSpan.Zero ? TimeSpan.Zero : due + TimeSpan.FromSeconds(5);
        if (due > TimeSpan.FromDays(1))
        {
            due = TimeSpan.FromDays(1);
        }

        ArmAcmeRetry(due);
    }

    private void ArmAcmeRetry(TimeSpan due)
    {
        try
        {
            _acmeRetry.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown disposed it between the check above and here. Nothing is left to retry.
            _logger.LogDebug("Not scheduling a Let's Encrypt retry during shutdown");
        }
    }

    private void RetryAcme()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        if (IsRunning && ReadSettings() is { Certificate: { Mode: CertificateMode.Automatic, AcceptTerms: true } } automatic)
        {
            _ = Task.Run(() => EnsureAutomaticCertificateAsync(automatic, _shutdown.Token), _shutdown.Token);
        }
    }

    private static List<string> ServedHostnames(ProxySettings settings)
        => settings.Hostnames.Concat(settings.Routes.Where(r => r.Enabled).SelectMany(r => r.Hostnames)).Distinct(StringComparer.Ordinal).ToList();

    // The names Let's Encrypt is asked for: Jellyfin's and internet-facing services', leaving out names it can
    // never check, such as a LAN-only service at nas.lan. One unverifiable name would fail the whole order.
    private static List<string> CertificateHostnames(ProxySettings settings)
        => settings.Hostnames
            .Concat(settings.Routes.Where(r => r.Enabled && r.Exposure != RouteExposure.LanOnly).SelectMany(r => r.Hostnames))
            .Where(HostnameMatcher.IsPublicName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Describes the proxy for the dashboard.
    /// </summary>
    /// <param name="takeover">The port takeover state.</param>
    /// <returns>The status.</returns>
    public ProxyStatus GetStatus(TakeoverState takeover)
    {
        ArgumentNullException.ThrowIfNull(takeover);
        var host = Volatile.Read(ref _host);
        var settings = ReadSettings();
        var options = ProxyOptions.From(settings.Advanced);
        return new ProxyStatus
        {
            Enabled = settings.Enabled,
            Running = host is not null,
            StartedUtc = host is null ? null : _startedUtc,
            Addresses = host?.Addresses.ToList() ?? new List<string>(),
            Error = _error,
            Backend = _jellyfin.Backend.ToString(),
            JellyfinHttpPort = _jellyfin.HttpPort,
            JellyfinHttpsPort = _jellyfin.HttpsPort,
            JellyfinTrustsProxy = _jellyfin.TrustsForwarding,
            DisableFlagPresent = File.Exists(_paths.DisableFlag),
            DisableFlagPath = _paths.DisableFlag,
            Certificate = _certificates.Status,
            Acme = _acme.Describe(CertificateHostnames(settings), ReadRecords()),
            // An older version's rollback left Jellyfin unmanaged, which is all the page needs to know.
            TakeoverPhase = takeover.Phase == TakeoverPhase.Degraded ? TakeoverPhase.Unmanaged : takeover.Phase,
            TakeoverAppliedUtc = takeover.AppliedUtc,
            TakeoverError = takeover.LastError,
            DetectedLanAddress = NetworkTakeover.DetectLanAddress()?.ToString() ?? string.Empty,
            HttpPort = options.HttpPort,
            HttpsPort = options.HttpsPort,
            LanAddress = options.LanAddress,
            PublicUrl = PublicUrl(settings),
            CountryRefused = _blocks.Refused(),
            CountryDatabaseLoaded = _geo.IsLoaded,
            ServerCountry = _blocks.ServerCountry.Country,
            JellyfinExposedAt = ExposedAt(),
            FolderProblem = FolderAccess.Problem,
            EveryoneLooksLocal = _localTraffic.Suspected,
            CountryRule = DescribeCountryRule(options, _blocks.ServerCountry.Country),
        };
    }

    // The country rule is on by default, and someone travelling abroad is the first to meet it, so the Proxy tab
    // says in plain words who it lets in.
    internal static string DescribeCountryRule(ProxyOptions options, string serverCountry)
    {
        var others = options.Countries.Count;
        var plus = others == 0 ? string.Empty : others == 1 ? " and 1 other country" : $" and {others} other countries";
        return options.CountryMode switch
        {
            CountryRuleMode.ServerCountry => serverCountry.Length > 0
                ? $"Only visitors from {serverCountry}{plus} can connect from the internet, so a user travelling abroad is refused."
                : $"Only visitors from this server's country{plus} will be able to connect from the internet, once its country is known.",
            CountryRuleMode.AllowOnly => others == 1
                ? "Only visitors from 1 chosen country can connect from the internet."
                : $"Only visitors from {others} chosen countries can connect from the internet.",
            CountryRuleMode.Block => others == 1 ? "Visitors from 1 country are refused." : $"Visitors from {others} countries are refused.",
            _ => string.Empty
        };
    }

    // Trusting the proxy turns on Jellyfin's remote access, which also lets the internet reach Jellyfin's own
    // ports directly wherever they are reachable: a public address on this machine is the case it can see.
    private string ExposedAt()
    {
        var network = _configuration.GetNetworkConfiguration();
        if (!network.EnableRemoteAccess)
        {
            return string.Empty;
        }

        // Bound to specific addresses: exposed when one of them is public. Bound to every interface, or to none,
        // exposed when the machine has a public address at all.
        var bound = network.LocalNetworkAddresses
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Cast<IPAddress>()
            .ToList();
        if (bound.Count > 0 && !bound.Any(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
        {
            return bound.FirstOrDefault(ip => !ip.IsIPv6LinkLocal && Models.IPAddressClassifier.IsPublic(ip))?.ToString() ?? string.Empty;
        }

        return NetworkTakeover.DetectPublicAddress()?.ToString() ?? string.Empty;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _shutdown.Dispose();
    }

    private List<int> JellyfinPortClash(ListenerOptions listener)
    {
        var jellyfin = new[] { _jellyfin.HttpPort, _jellyfin.HttpsPort };
        return new[] { listener.HttpPort, listener.HttpsPort ?? 0 }
            .Where(p => p != 0 && jellyfin.Contains(p))
            .Distinct()
            .ToList();
    }

    // The internet always reaches HTTPS on port 443, so the address needs no port.
    private static string PublicUrl(ProxySettings settings)
    {
        var name = settings.Hostnames.FirstOrDefault(h => !h.StartsWith('*'));
        return name is null ? string.Empty : "https://" + name;
    }

    // Copies, so the certificate work never holds the configuration's own records.
    private static List<Models.DNSRecord> ReadRecords()
        => Plugin.Instance?.ReadConfiguration(c => c.Records.Select(r => r.Clone()).ToList()) ?? new List<Models.DNSRecord>();

    private ProxySettings ReadSettings()
    {
        var settings = Plugin.Instance?.ReadConfiguration(c => ProxyRuntimeFactory.Clone(c.Proxy)) ?? new ProxySettings();
        ProxySettingsValidator.Normalize(settings);
        return settings;
    }

    private ProxyRuntime BuildRuntime(ProxySettings settings)
    {
        // An automatic certificate only ever covers the names Let's Encrypt can check.
        var hostnames = settings.Certificate.Mode == CertificateMode.Automatic ? CertificateHostnames(settings) : ServedHostnames(settings);
        _certificates.Configure(settings.Certificate, hostnames);
        return ProxyRuntimeFactory.Build(settings, _jellyfin, IsLan, _certificates.HasCertificate);
    }

    // A certificate file appeared, or became unreadable: rebuild so the HTTPS listener follows it.
    private void OnCertificateAvailabilityChanged(object? sender, EventArgs e)
        => _ = Task.Run(async () =>
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ApplyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning("Applying a changed certificate failed: {Message}", ex.Message);
            }
        });

    private bool IsLan(IPAddress address) => _network.IsInLocalNetwork(address);

    private ProxyHostOptions HostOptions(ProxyRuntimeHolder holder) => new()
    {
        Holder = holder,
        Certificates = _certificates,
        Challenges = _challenges,
        LoggerFactory = _loggerFactory,
        AccessRecorder = _access,
        FailureTracker = _bans,
        LocalTraffic = _localTraffic,
        ConnectionFilters = new IConnectionFilter[] { _blocks },
        ContentRoot = EnsureDataDirectory()
    };

    private void ReportEveryoneLooksLocal()
    {
        const string Why = "Something in front of the proxy, such as rootless Docker, a tunnel or another proxy, may be hiding visitors' addresses. Bans, the block list and country rules cannot apply until it passes them on.";
        _logger.LogWarning("Every visitor in the last hour came from one local address. {Why}", Why);
        _activity?.Log(
            "Remote Access sees every visitor as local",
            "DynamicDNS.EveryoneLooksLocal",
            $"{LocalTrafficWatch.Threshold} requests for your public names came from one local address and none from the internet. " + Why + " See the Proxy tab.",
            LogLevel.Warning);
    }

    private string EnsureDataDirectory()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        return _paths.DataDirectory;
    }

    // Caller holds _lifecycle.
    private async Task StartHostAsync(ProxyRuntime runtime, CancellationToken cancellationToken)
    {
        var holder = new ProxyRuntimeHolder(runtime);
        try
        {
            var host = await ProxyHost.StartAsync(HostOptions(holder), cancellationToken).ConfigureAwait(false);
            _holder = holder;
            _startedUtc = DateTime.UtcNow;
            _error = string.Empty;
            Volatile.Write(ref _host, host);
            var addresses = string.Join(", ", host.Addresses);
            _logger.LogInformation("Reverse proxy listening on {Addresses}", addresses);
            if (_reportedError.Length > 0)
            {
                _reportedError = string.Empty;
                _activity?.Log("Remote Access started its proxy", "DynamicDNS.ProxyStarted", "It is listening on " + addresses + ".");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            _error = DescribeStartError(ex, runtime.Listener);
            _logger.LogError("Reverse proxy could not start: {Error}", _error);
            if (!string.Equals(_error, _reportedError, StringComparison.Ordinal))
            {
                _reportedError = _error;
                _activity?.Log("Remote Access could not start its proxy", "DynamicDNS.ProxyStartFailed", _error, LogLevel.Error);
            }
        }
    }

    // Caller holds _lifecycle.
    private async Task StopHostAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        _holder = null;
        if (host is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_stopTimeout);
        await host.StopAsync(timeout.Token).ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("Reverse proxy stopped");
    }

    private string DescribeStartError(Exception ex, ListenerOptions listener)
    {
        var socket = FindSocketError(ex);
        var ports = listener.HttpsPort is { } https ? $"{listener.HttpPort} and {https}" : listener.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (socket == SocketError.AccessDenied)
        {
            return OperatingSystem.IsLinux()
                ? $"Jellyfin is not allowed to use port {ports}. Add AmbientCapabilities=CAP_NET_BIND_SERVICE to Jellyfin's service (sudo systemctl edit jellyfin), then restart Jellyfin. In Docker, publish ports 80 and 443 to the container."
                : $"Jellyfin is not allowed to use port {ports}.";
        }

        // Jellyfin's own ports are refused before binding, so a clash here is another program.
        if (socket == SocketError.AddressAlreadyInUse || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return $"Port {ports} is already in use by another program.";
        }

        return CredentialRedactor.Redact(ex.Message);
    }

    private static SocketError? FindSocketError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return socket.SocketErrorCode;
            }
        }

        return null;
    }

    private async Task<string?> CheckJellyfinAsync(Uri proxyAddress, string baseUrl, CancellationToken cancellationToken)
    {
        // Ask for loopback explicitly: a probe listening on "localhost" may resolve either way.
        var target = new UriBuilder(proxyAddress) { Host = IPAddress.Loopback.ToString(), Path = baseUrl + "/System/Info/Public" }.Uri;
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.GetAsync(target, cancellationToken).ConfigureAwait(false);

            // With "Require HTTPS" on, Jellyfin sends this plain-HTTP check on to its own HTTPS port. That is still
            // Jellyfin answering through the proxy; real visitors arrive over HTTPS and are not redirected.
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location?.Scheme == Uri.UriSchemeHttps)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return $"Jellyfin answered {(int)response.StatusCode} through the proxy.";
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            var id = json.RootElement.TryGetProperty("Id", out var value) ? value.GetString() : null;
            return SameId(id, _systemId) ? null : "Something other than this Jellyfin server answered through the proxy.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Jellyfin could not be reached through the proxy: " + CredentialRedactor.Redact(ex.Message);
        }
    }

    private static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.Replace("-", string.Empty, StringComparison.Ordinal), b.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
}
