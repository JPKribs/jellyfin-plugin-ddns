using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Loads the proxy's certificate, keeps it current when its files change, and picks it for each TLS
/// handshake. The private key never leaves this class except inside the handshake context it hands Kestrel.
/// </summary>
public sealed class CertificateStore : IDisposable
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<CertificateStore> _logger;
    private readonly ActivityLogger? _activity;
    private readonly SecretProtector _secrets;
    private readonly ProxyPaths _paths;
    private readonly object _lock = new();
    private Timer? _poll;
    private bool _disposed;
    private RootCertificate? _root;

    // The files' stamp when they last failed to load, so a file left broken is reported once, not every minute.
    private string _failedStamp = string.Empty;
    private Loaded? _current;
    private ProxyCertificateSettings _settings = new();
    private IReadOnlyList<string> _hostnames = Array.Empty<string>();
    private CertificateStatus _status = new();
    private string _configured = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateStore"/> class.
    /// </summary>
    /// <param name="paths">The proxy's data paths.</param>
    /// <param name="secrets">The secret protector for the certificate password.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">The activity log writer. Passed in rather than shared, so each server, and each test, writes to its own.</param>
    public CertificateStore(ProxyPaths paths, SecretProtector secrets, ILogger<CertificateStore> logger, ActivityLogger? activity = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _secrets = secrets;
        _logger = logger;
        _activity = activity;
    }

    /// <summary>
    /// Raised when a file change makes a certificate usable or unusable, so the proxy can start or stop its
    /// HTTPS listener without waiting for a save.
    /// </summary>
    public event EventHandler? AvailabilityChanged;

    /// <summary>Gets a value indicating whether a usable certificate is loaded.</summary>
    public bool HasCertificate => Volatile.Read(ref _current) is not null;

    /// <summary>Gets the current status for the dashboard.</summary>
    public CertificateStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Applies certificate settings: loads the certificate when they or the hostnames changed, and watches its
    /// files for changes. Unchanged settings keep the loaded certificate; the file watch covers renewals.
    /// </summary>
    /// <param name="settings">The certificate settings.</param>
    /// <param name="hostnames">Every hostname the proxy serves, to report coverage and to name a self-signed certificate.</param>
    public void Configure(ProxyCertificateSettings settings, IReadOnlyList<string> hostnames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostnames);
        var key = string.Join('\n', settings.Mode, settings.Path, settings.KeyPath, settings.Password, string.Join(',', hostnames));
        lock (_lock)
        {
            if (_disposed || string.Equals(key, _configured, StringComparison.Ordinal))
            {
                return;
            }

            _configured = key;
            _settings = settings;
            _hostnames = hostnames;
            Reload(force: true, settingsChanged: true);
            _poll?.Dispose();
            _poll = settings.Mode is CertificateMode.Pkcs12File or CertificateMode.PemFiles or CertificateMode.Automatic
                ? new Timer(_ => Poll(), null, _pollInterval, _pollInterval)
                : null;
        }
    }

    /// <summary>
    /// Chooses the certificate for a handshake. Internet clients must ask for a name the certificate covers;
    /// anything else is refused so scanners probing the bare address get no certificate. Local clients get the
    /// certificate regardless, so connecting by IP on the LAN still works.
    /// </summary>
    /// <param name="serverName">The SNI name the client asked for, if any.</param>
    /// <param name="local">Whether the client is on the LAN or a trusted network.</param>
    /// <returns>The handshake context, or null to refuse.</returns>
    public SslStreamCertificateContext? Select(string? serverName, bool local)
    {
        var current = Volatile.Read(ref _current);
        if (current is null)
        {
            return null;
        }

        if (local)
        {
            return current.Context;
        }

        return !string.IsNullOrEmpty(serverName) && current.Leaf.MatchesHostname(serverName) ? current.Context : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _poll?.Dispose();
            _poll = null;
            Interlocked.Exchange(ref _current, null)?.Dispose();
        }
    }

    /// <summary>
    /// Reloads the certificate now, replacing a self-signed one that is close to expiring.
    /// </summary>
    public void Refresh()
    {
        bool before;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            before = HasCertificate;
            Reload(force: true, settingsChanged: false);
        }

        RaiseIfFlipped(before);
    }

    private void Poll()
    {
        bool before;
        lock (_lock)
        {
            // A tick already running when the store was disposed must not load a certificate nothing disposes.
            if (_disposed)
            {
                return;
            }

            before = HasCertificate;
            Reload(force: false, settingsChanged: false);
        }

        RaiseIfFlipped(before);
    }

    // Raised outside the lock: the handler applies settings, which configures this store again.
    private void RaiseIfFlipped(bool before)
    {
        if (before != HasCertificate)
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Caller holds _lock.
    private void Reload(bool force, bool settingsChanged)
    {
        var stamp = FileStamp(_settings);
        var current = Volatile.Read(ref _current);
        if (!force && ((current is not null && current.Stamp == stamp) || stamp == _failedStamp))
        {
            return;
        }

        try
        {
            var loaded = _settings.Mode switch
            {
                CertificateMode.Pkcs12File => LoadPkcs12(_settings, stamp),
                CertificateMode.PemFiles => LoadPem(_settings.Path, _settings.KeyPath, _secrets.Unprotect(_settings.Password), stamp),
                CertificateMode.Automatic => LoadIssued(stamp),
                CertificateMode.SelfSigned => LoadSelfSigned(),
                _ => null
            };

            // The previous certificate is left to the garbage collector rather than disposed: a handshake that
            // started a moment ago may still be using it.
            Interlocked.Exchange(ref _current, loaded);
            _status = Describe(loaded, string.Empty);
            _failedStamp = string.Empty;
            if (loaded is not null && !force)
            {
                _logger.LogInformation("Proxy certificate reloaded: {Subject}, expires {NotAfter:u}", loaded.Leaf.Subject, loaded.Leaf.NotAfter.ToUniversalTime());
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            _failedStamp = stamp;
            _logger.LogWarning("Proxy certificate could not be loaded: {Message}", ex.Message);
            if (settingsChanged)
            {
                // The settings changed, so the old certificate is no longer the one asked for: serving it on would
                // hide the problem behind a green status.
                Interlocked.Exchange(ref _current, null);
                _status = Describe(null, ex.Message);
            }
            else
            {
                // A file caught half-written during a renewal, or a brief read failure: keep serving the previous
                // one, so live streams are not cut, until the next poll.
                _status = Describe(current, ex.Message);
            }
        }
    }

    private CertificateStatus Describe(Loaded? loaded, string error)
    {
        var status = new CertificateStatus { Mode = _settings.Mode, Error = error };
        if (loaded is null)
        {
            return status;
        }

        var leaf = loaded.Leaf;
        status.Loaded = true;
        status.Subject = leaf.Subject;
        status.Issuer = leaf.Issuer;
        status.NotAfterUtc = leaf.NotAfter.ToUniversalTime();
        status.NotBeforeUtc = leaf.NotBefore.ToUniversalTime();
        status.Names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        status.UncoveredHostnames = _hostnames.Where(h => !Covers(leaf, h)).ToList();
        return status;
    }

    private static bool Covers(X509Certificate2 leaf, string hostname)
    {
        // A configured wildcard is covered by the identical wildcard SAN; test it with a sample name.
        var probe = hostname.StartsWith("*.", StringComparison.Ordinal) ? "probe" + hostname[1..] : hostname;
        return leaf.MatchesHostname(probe);
    }

    private Loaded LoadPkcs12(ProxyCertificateSettings settings, string stamp)
    {
        var password = _secrets.Unprotect(settings.Password);
        var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(settings.Path, string.IsNullOrEmpty(password) ? null : password);
        // A file holding several keyed certificates serves a valid one before an expired one, then the one that
        // covers the most of the proxy's names, then the newest, so the order they were exported in does not matter.
        var now = DateTime.UtcNow;
        var leaf = all.Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore.ToUniversalTime() <= now && now < c.NotAfter.ToUniversalTime())
            .ThenByDescending(c => _hostnames.Count(h => Covers(c, h)))
            .ThenByDescending(c => c.NotAfter)
            .FirstOrDefault()
            ?? throw new CryptographicException("The certificate file has no private key.");

        // Only certificates without a key are chain certificates; other keyed ones would show their names in
        // every handshake.
        var intermediates = new X509Certificate2Collection(all.Where(c => !c.HasPrivateKey).ToArray());
        return Build(leaf, intermediates, stamp);
    }

    // The certificate Let's Encrypt issued, once there is one; until then there is nothing to load.
    private Loaded? LoadIssued(string stamp)
    {
        if (!File.Exists(_paths.AcmeChainFile))
        {
            return null;
        }

        // A chain file holding its key needs nothing else; an older one needs the separate key file.
        if (ChainHoldsKey())
        {
            return LoadPem(_paths.AcmeChainFile, _paths.AcmeChainFile, string.Empty, stamp);
        }

        return File.Exists(_paths.AcmeKeyFile) ? LoadPem(_paths.AcmeChainFile, _paths.AcmeKeyFile, string.Empty, stamp) : null;
    }

    // Since the key is written into the chain file, the two can never be caught out of step; a chain file from
    // an older version holds certificates only, and its key is in the separate file.
    private bool ChainHoldsKey()
        => File.ReadAllText(_paths.AcmeChainFile).Contains("PRIVATE KEY-----", StringComparison.Ordinal);

    private static Loaded LoadPem(string path, string keyPath, string password, string stamp)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(path);
        using var withKey = string.IsNullOrEmpty(password)
            ? X509Certificate2.CreateFromPemFile(path, keyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);

        // Keys loaded from PEM are ephemeral, which TLS on Windows and macOS refuses. A PKCS#12 round trip
        // gives the key a form every platform's TLS stack accepts.
        var leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        var intermediates = new X509Certificate2Collection(chain.Skip(1).ToArray());
        return Build(leaf, intermediates, stamp);
    }

    // The self-signed option: a certificate issued from the plugin's own root, so a device that trusts the root
    // once trusts every certificate issued after. One made before the root existed is replaced by an issued one.
    private Loaded LoadSelfSigned()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var root = LocalAuthority.LoadOrCreateRoot(_paths.SelfSignedRootFile, out var created);
        Interlocked.Exchange(ref _root, new RootCertificate(root.ExportCertificatePem(), LocalAuthority.Fingerprint(root), root.NotAfter.ToUniversalTime()));
        if (created)
        {
            _logger.LogInformation("Created the root certificate for self-signed HTTPS; install it on each device from the Proxy tab");
            _activity?.Log(
                "Remote Access created a root certificate",
                "DynamicDNS.RootCertificateCreated",
                "Devices that should trust the self-signed certificate need this root installed once. Download it from the Proxy tab.");
        }

        var path = _paths.SelfSignedFile;
        var wanted = _hostnames.Append("localhost").Distinct(StringComparer.Ordinal).ToList();
        if (File.Exists(path))
        {
            X509Certificate2? existing = null;
            try
            {
                existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            }
            catch (CryptographicException ex)
            {
                // A damaged file is replaced below rather than leaving the proxy without a certificate.
                _logger.LogWarning("The self-signed certificate could not be read and is being replaced: {Message}", ex.Message);
            }

            if (existing is not null
                && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30)
                && wanted.All(h => Covers(existing, h))
                && LocalAuthority.IssuedBy(existing, root))
            {
                root.Dispose();
                return Build(existing, new X509Certificate2Collection(), "self-signed");
            }

            existing?.Dispose();
        }

        using (root)
        {
            var issued = LocalAuthority.Issue(root, wanted);
            SecretFile.Write(path, issued);
            _logger.LogInformation("Issued a self-signed proxy certificate for {Names}", string.Join(", ", wanted));
            return Build(X509CertificateLoader.LoadPkcs12(issued, null), new X509Certificate2Collection(), "self-signed");
        }
    }

    /// <summary>
    /// Gets the root the self-signed certificate is issued from, for devices to install; null until one is made.
    /// </summary>
    public RootCertificate? Root => Volatile.Read(ref _root);

    private static Loaded Build(X509Certificate2 leaf, X509Certificate2Collection intermediates, string stamp)
        => new(leaf, SslStreamCertificateContext.Create(leaf, intermediates, offline: true), stamp);

    private string FileStamp(ProxyCertificateSettings settings)
    {

        return settings.Mode switch
        {
            CertificateMode.Pkcs12File => FileStamp(settings.Path),
            CertificateMode.PemFiles => FileStamp(settings.Path) + "|" + FileStamp(settings.KeyPath),
            CertificateMode.Automatic => FileStamp(_paths.AcmeChainFile) + "|" + FileStamp(_paths.AcmeKeyFile),
            _ => settings.Mode.ToString()
        };
    }

    /// <summary>
    /// What a certificate file looks like on disk, to notice it changed. A link, as certbot's <c>live</c> folder
    /// uses, is followed to the file it points at: a renewal can repoint the link and leave the link's own time
    /// alone. The size counts too, for copies that keep the old time.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>A value that changes when the file does.</returns>
    internal static string FileStamp(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return "missing";
        }

        FileSystemInfo file = new FileInfo(path);
        try
        {
            file = file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
        }
        catch (IOException)
        {
            // A broken or looping link reads as the link itself.
        }

        var length = file is FileInfo info && info.Exists ? info.Length : -1;
        return string.Join(':', file.FullName, file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class Loaded : IDisposable
    {
        public Loaded(X509Certificate2 leaf, SslStreamCertificateContext context, string stamp)
        {
            Leaf = leaf;
            Context = context;
            Stamp = stamp;
        }

        public X509Certificate2 Leaf { get; }

        public SslStreamCertificateContext Context { get; }

        public string Stamp { get; }

        public void Dispose() => Leaf.Dispose();
    }
}
