using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// Administrative endpoints backing the Proxy, Services and Advanced tabs.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DynamicDns/Proxy")]
[Produces("application/json")]
public class ProxyController : ControllerBase
{
    private readonly ProxyManager _proxy;
    private readonly JellyfinNetworkOrchestrator _orchestrator;
    private readonly SecretProtector _secrets;
    private readonly ActivityLogger _activity;
    private readonly GeoIpService _geo;
    private readonly ServerLocation _server;
    private readonly INetworkManager _network;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyController"/> class.
    /// </summary>
    /// <param name="proxy">The proxy manager.</param>
    /// <param name="orchestrator">The port takeover orchestrator.</param>
    /// <param name="secrets">The secret protector.</param>
    /// <param name="activity">The activity log writer.</param>
    /// <param name="geo">The location lookup, to check country rules against the person saving them.</param>
    /// <param name="server">The country this server is in.</param>
    /// <param name="network">Jellyfin's network manager, for the LAN test.</param>
    public ProxyController(ProxyManager proxy, JellyfinNetworkOrchestrator orchestrator, SecretProtector secrets, ActivityLogger activity, GeoIpService geo, ServerLocation server, INetworkManager network)
    {
        _geo = geo;
        _server = server;
        _network = network;
        _proxy = proxy;
        _orchestrator = orchestrator;
        _secrets = secrets;
        _activity = activity;
    }

    /// <summary>
    /// Returns the proxy settings with the certificate password replaced by a placeholder.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ProxySettings> GetSettings()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        return Ok(Redact(plugin.ReadConfiguration(c => ProxyRuntimeFactory.Clone(c.Proxy))));
    }

    /// <summary>
    /// Saves the proxy settings and applies them to the running proxy. Never changes Jellyfin's own network
    /// settings; that only happens through <see cref="TakeOver"/>.
    /// </summary>
    /// <param name="incoming">The settings from the dashboard.</param>
    /// <returns>The saved settings and the new status, or the problems that stopped the save.</returns>
    [HttpPost("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> SaveSettings([FromBody] ProxySettings incoming)
    {
        var plugin = Plugin.Instance;
        if (incoming is null || plugin is null)
        {
            return BadRequest();
        }

        var rawHostnames = incoming.Hostnames?.ToList() ?? new List<string>();
        var stored = plugin.ReadConfiguration(c => ProxyRuntimeFactory.Clone(c.Proxy));
        incoming.Certificate ??= new ProxyCertificateSettings();
        // A kept password belongs to the file it was entered for. Switching source or file drops it, so an old
        // .pfx password is never tried against a new, unencrypted key.
        var sameFile = incoming.Certificate.Mode == stored.Certificate.Mode
            && string.Equals((incoming.Certificate.Path ?? string.Empty).Trim(), stored.Certificate.Path, StringComparison.Ordinal)
            && (incoming.Certificate.Mode != CertificateMode.PemFiles
                || string.Equals((incoming.Certificate.KeyPath ?? string.Empty).Trim(), stored.Certificate.KeyPath, StringComparison.Ordinal));
        if (XmlText.FindInvalid(incoming) is { } bad)
        {
            return BadRequest(new { Errors = new List<string> { $"{bad} holds a character that cannot be saved." } });
        }

        if (SecretProtector.IsProtected(incoming.Certificate.Password))
        {
            return BadRequest(new { Errors = new List<string> { "An encrypted value cannot be entered as the certificate password." } });
        }

        incoming.Certificate.Password = _secrets.ResolveIncoming(incoming.Certificate.Password, sameFile ? stored.Certificate.Password : string.Empty);
        ProxySettingsValidator.Normalize(incoming);

        var options = ProxyOptions.From(incoming.Advanced);
        var reserved = new[] { _proxy.Jellyfin.HttpPort, _proxy.Jellyfin.HttpsPort, options.HttpPort, options.HttpsPort };
        var errors = ProxySettingsValidator.Validate(incoming, rawHostnames, reserved);
        if (!incoming.Enabled && _orchestrator.State.Snapshot is not null)
        {
            errors.Add("Undo Trust the Proxy before turning the proxy off, so Jellyfin's remote access goes back to how it was.");
        }

        var lockout = SelfLockout(incoming);
        if (lockout is not null)
        {
            errors.Add(lockout);
        }

        if (errors.Count > 0)
        {
            return BadRequest(new { Errors = errors });
        }

        if (await SelfPointingServices(stored, incoming).ConfigureAwait(false) is { } self)
        {
            return BadRequest(new { Errors = new List<string> { self } });
        }

        // Binding Jellyfin to this machine is part of trusting the proxy, so while it is trusted a change to it is
        // applied the same way, with Jellyfin restarting once: a change that cannot be applied is refused, so the
        // setting never claims what Jellyfin is not doing. Otherwise it waits for Trust the Proxy. Turning it off
        // is applied first, since it needs nothing of the proxy. Turning it on is applied last, once the proxy
        // runs with the new settings, so the check that the proxy works is made against them.
        string? restart = null;
        var user = ActivityUser.Of(User);
        var loopbackNow = options.JellyfinLoopbackOnly;
        var loopbackChanged = ProxyOptions.From(stored.Advanced).JellyfinLoopbackOnly != loopbackNow;
        if (loopbackChanged && !loopbackNow)
        {
            var result = await _orchestrator.ApplyLoopbackChangeAsync(incoming, _proxy, user).ConfigureAwait(false);
            if (!result.Success)
            {
                return BadRequest(new { Errors = new List<string> { result.Message } });
            }

            restart = result.Restarting ? "Jellyfin is restarting so the change takes effect." : null;
        }

        // While Jellyfin listens on this machine only, the proxy is the only way in. A save that stops a working
        // proxy, such as a port another program holds, is undone and refused.
        var wasRunning = _proxy.IsRunning;
        plugin.MutateConfiguration(c =>
        {
            c.Proxy = incoming;
            return true;
        });
        await _proxy.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
        if (incoming.Enabled && wasRunning && !_proxy.IsRunning && _orchestrator.IsBoundToLoopback)
        {
            var why = await _proxy.CheckRunningAsync(CancellationToken.None).ConfigureAwait(false);
            await PutBackAsync(plugin, stored).ConfigureAwait(false);
            return BadRequest(new { Errors = new List<string> { "The proxy could not start with these settings, so they were not saved. Jellyfin only listens on this machine, so the proxy has to keep running. " + why } });
        }

        if (loopbackChanged && loopbackNow)
        {
            var result = await _orchestrator.ApplyLoopbackChangeAsync(incoming, _proxy, user).ConfigureAwait(false);
            if (!result.Success)
            {
                await PutBackAsync(plugin, stored).ConfigureAwait(false);
                return BadRequest(new { Errors = new List<string> { result.Message } });
            }

            restart = result.Restarting ? "Jellyfin is restarting so the change takes effect." : null;
        }
        else if (!loopbackChanged)
        {
            // New hostnames change the address Jellyfin gives its clients. Jellyfin reads that again when its
            // network settings are saved, so this needs no restart. A loopback change above already wrote it.
            _orchestrator.UpdatePublishedAddresses(incoming, user);
        }

        var changed = ChangedSections(stored, incoming);
        if (changed.Count > 0)
        {
            _activity.Log(
                "Remote Access settings were changed",
                "DynamicDNS.ProxySettingsChanged",
                "Changed: " + string.Join(", ", changed) + ". The proxy is " + (incoming.Enabled ? "on." : "off."),
                userId: user);
        }

        return Ok(new { Settings = Redact(ProxyRuntimeFactory.Clone(incoming)), Status = _proxy.GetStatus(_orchestrator.State), Restart = restart });
    }

    /// <summary>
    /// Returns the proxy's status.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ProxyStatus> GetStatus() => Ok(_proxy.GetStatus(_orchestrator.State));

    /// <summary>
    /// Returns the hostnames the DDNS records keep pointed at this server, for the hostname pickers.
    /// </summary>
    /// <returns>The hostnames with the record each comes from.</returns>
    [HttpGet("DdnsHostnames")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DdnsHostname>> GetDdnsHostnames()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        return Ok(plugin.ReadConfiguration(c => DdnsHostnames.From(c.Records)));
    }

    /// <summary>
    /// Returns the built-in defaults, so the dashboard can show what an empty field means, along with both sets
    /// and which one this server's memory picked.
    /// </summary>
    /// <returns>The defaults.</returns>
    [HttpGet("Defaults")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ProxyDefaults> GetDefaults() => Ok(new ProxyDefaults
    {
        Values = ProxyOptions.Defaults,
        Standard = ProxyOptions.Standard,
        LowMemory = ProxyOptions.LowMemory,
        IsLowMemory = ProxyOptions.IsLowMemory,
        MemoryMb = ProxyOptions.MemoryBytes / (1024 * 1024),
        LowMemoryBelowMb = ProxyOptions.LowMemoryBelowBytes / (1024 * 1024)
    });

    /// <summary>
    /// Moves Jellyfin to its backend port so the proxy can take Jellyfin's ports, then restarts Jellyfin.
    /// Checks first that the proxy can reach this Jellyfin and that the ports are free.
    /// </summary>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("TakeOver")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TakeoverResult>> TakeOver(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var settings = plugin.ReadConfiguration(c => ProxyRuntimeFactory.Clone(c.Proxy));
        ProxySettingsValidator.Normalize(settings);
        return Ok(await _orchestrator.ApplyAsync(settings, _proxy, cancellationToken, ActivityUser.Of(User)).ConfigureAwait(false));
    }

    /// <summary>
    /// Puts Jellyfin's original network settings back and restarts Jellyfin.
    /// </summary>
    /// <returns>The outcome.</returns>
    [HttpPost("Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TakeoverResult> Restore()
        => Ok(_orchestrator.Restore("Undone from the Remote Access page.", restart: true, ActivityUser.Of(User)));

    /// <summary>
    /// Returns the root the self-signed certificate is issued from, for installing on devices. It is public: it
    /// carries no private key.
    /// </summary>
    /// <returns>The root as PEM with its fingerprint, or 404 when the certificate is not self-signed.</returns>
    [HttpGet("Certificate/Root")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RootCertificate> GetRootCertificate()
        => _proxy.SelfSignedRoot is { } root ? Ok(root) : NotFound();

    /// <summary>
    /// Orders a new Let's Encrypt certificate now, even when the current one is fine.
    /// </summary>
    /// <returns>Whether one was issued, and the message to show.</returns>
    [HttpPost("Certificate/Request")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> RequestCertificate()
    {
        _activity.Log("Remote Access was asked for a new certificate", "DynamicDNS.CertificateRequested", "Requested from the Remote Access page.", userId: ActivityUser.Of(User));
        var (ok, message) = await _proxy.RequestCertificateAsync(CancellationToken.None).ConfigureAwait(false);
        return Ok(new { Ok = ok, Message = message, Status = _proxy.GetStatus(_orchestrator.State) });
    }

    /// <summary>
    /// Restarts the proxy (not Jellyfin), for example after fixing a port conflict.
    /// </summary>
    /// <returns>The new status.</returns>
    [HttpPost("Restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProxyStatus>> RestartProxy()
    {
        // While Jellyfin listens on this machine only, a working proxy is not stopped: if it could not bind its
        // ports again, nothing would reach Jellyfin. A stopped one is still started, which is what the button is
        // for after a port conflict.
        if (!(_proxy.IsRunning && _orchestrator.IsBoundToLoopback))
        {
            await _proxy.StopAsync().ConfigureAwait(false);
        }

        await _proxy.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
        return Ok(_proxy.GetStatus(_orchestrator.State));
    }

    // Puts the settings from before a refused save back, and the proxy with them.
    private async Task PutBackAsync(Plugin plugin, ProxySettings stored)
    {
        plugin.MutateConfiguration(c =>
        {
            c.Proxy = stored;
            return true;
        });
        await _proxy.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // A Service whose target answers as this Jellyfin server would serve Jellyfin past the rules meant for it.
    // Only targets new or changed in this save are asked, so saving another tab never waits on a Service that
    // is down. A target that does not answer is allowed.
    private async Task<string?> SelfPointingServices(ProxySettings stored, ProxySettings incoming)
    {
        var before = stored.Routes.Where(r => r.Enabled).Select(r => r.Id + "\n" + r.Target).ToHashSet(StringComparer.Ordinal);
        var asked = incoming.Routes.Where(r => r.Enabled && !before.Contains(r.Id + "\n" + r.Target)).ToList();
        if (asked.Count == 0)
        {
            return null;
        }

        using var strict = new HttpClient(ProbeHandler(acceptAnyCertificate: false)) { MaxResponseContentBufferSize = 64 * 1024 };
        using var lenient = new HttpClient(ProbeHandler(acceptAnyCertificate: true)) { MaxResponseContentBufferSize = 64 * 1024 };
        var found = await SelfTarget.FindJellyfinTargetsAsync(
            asked,
            _proxy.SystemId,
            (route, request, token) => (route.BackendTls == BackendTlsMode.Verify ? strict : lenient).SendAsync(request, token),
            TimeSpan.FromSeconds(3),
            HttpContext?.RequestAborted ?? CancellationToken.None).ConfigureAwait(false);
        return found.Count == 0
            ? null
            : string.Join(" ", found.Select(r => (r.Name.Length > 0 ? r.Name : "This Service") + " points at Jellyfin itself."));
    }

    // Never follows a redirect or a system proxy, and never connects to a cloud metadata address, whatever the
    // name resolves to. A Service set to skip or pin its certificate is asked without checking the certificate,
    // since only the answer's server id is read.
    private static SocketsHttpHandler ProbeHandler(bool acceptAnyCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                var allowed = addresses.Where(a => !ProxySettingsValidator.IsMetadataAddress(a)).ToArray();
                if (allowed.Length == 0)
                {
                    throw new HttpRequestException(host + " is not an address a Service may reach.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        if (acceptAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null;
        }

        return handler;
    }

    // What a save changed, in the page's own words, for the activity entry. Unchanged saves log nothing.
    private static List<string> ChangedSections(ProxySettings before, ProxySettings after)
    {
        static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);
        var changed = new List<string>();
        if (before.Enabled != after.Enabled)
        {
            changed.Add(after.Enabled ? "remote access turned on" : "remote access turned off");
        }

        if (Json(before.Hostnames) != Json(after.Hostnames))
        {
            changed.Add("addresses");
        }

        var a = before.Certificate;
        var b = after.Certificate;
        if (a.Mode != b.Mode || a.Path != b.Path || a.KeyPath != b.KeyPath || a.AcceptTerms != b.AcceptTerms || a.Password != b.Password)
        {
            changed.Add("certificate");
        }

        if (Json(before.Routes) != Json(after.Routes))
        {
            changed.Add("Services");
        }

        if (Json(before.Advanced) != Json(after.Advanced))
        {
            changed.Add("Advanced settings");
        }

        return changed;
    }

    // Blocking never applies to the LAN, but an administrator saving from the internet could cut themselves off.
    private string? SelfLockout(ProxySettings settings)
    {
        var options = ProxyOptions.From(settings.Advanced);
        var client = HttpContext?.Connection.RemoteIpAddress;
        if (!settings.Enabled || client is null || (options.BlockedAddresses.Count == 0 && !CountryRules.NeedsDatabase(options)))
        {
            return null;
        }

        var ip = NetworkClassifier.Normalize(client);
        if (BlockGuard.IsExempt(ip, _network, NetworkClassifier.ParseNetworks(options.TrustedNetworks)))
        {
            return null;
        }

        if (NetworkClassifier.ParseNetworks(options.BlockedAddresses).Any(n => n.Contains(ip)))
        {
            return $"The blocked addresses include your own address, {ip}. Remove it, or change the list from your home network.";
        }

        if (!CountryRules.NeedsDatabase(options))
        {
            return null;
        }

        var location = _geo.Lookup(ip);
        if (CountryRules.Allows(options, _geo.IsLoaded, location.CountryCode, _server.Location.CountryCode))
        {
            return null;
        }

        var where = location.Country.Length > 0 ? location.Country : "a country the location database does not know";
        return $"These country rules would block your own connection from {where}. Allow it, or change the rules from your home network.";
    }

    private static ProxySettings Redact(ProxySettings settings)
    {
        settings.Certificate.Password = SecretProtector.HasSecret(settings.Certificate.Password) ? SecretProtector.KeptSentinel : string.Empty;
        return settings;
    }
}
