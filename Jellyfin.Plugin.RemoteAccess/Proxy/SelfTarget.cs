using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Recognises a Service target that is this machine on Jellyfin's or the proxy's own port. Through such a
/// route Jellyfin would skip every internet rule meant for it, and the proxy would forward to itself until
/// every connection is used.
/// </summary>
public static class SelfTarget
{
    /// <summary>
    /// Whether a target is this machine on one of the given ports.
    /// </summary>
    /// <param name="target">The target address.</param>
    /// <param name="ports">Jellyfin's and the proxy's ports; zero entries are ignored.</param>
    /// <returns>True when the target is reserved.</returns>
    public static bool IsReserved(Uri target, IEnumerable<int> ports)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ports);
        return ports.Where(p => p > 0).Contains(target.Port) && IsThisMachine(target.Host);
    }

    /// <summary>
    /// Whether a host names this machine: loopback, localhost, this machine's name, or one of its addresses.
    /// </summary>
    /// <param name="host">The host, as in a URL.</param>
    /// <returns>True for this machine.</returns>
    public static bool IsThisMachine(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(name, out var ip))
        {
            return false;
        }

        return IsThisMachine(ip);
    }

    /// <summary>
    /// Whether an address, such as one a Service's name resolved to, is this machine.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>True for loopback, the unspecified address, or one of this machine's addresses.</returns>
    public static bool IsThisMachine(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = NetworkClassifier.Normalize(address);
        return IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.Any)
            || ip.Equals(IPAddress.IPv6Any)
            || LocalAddresses().Any(a => NetworkClassifier.Normalize(a).Equals(ip));
    }

    /// <summary>
    /// Whether two Jellyfin server ids are the same, with or without dashes.
    /// </summary>
    /// <param name="a">One id.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when both are set and equal.</returns>
    public static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.Replace("-", string.Empty, StringComparison.Ordinal), b.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the enabled Services whose target answers as this Jellyfin server, under any name or address, which
    /// the port check cannot see, for example through another machine forwarding to it. Each target is asked for
    /// Jellyfin's public system info. A target that does not answer, or answers as anything else, passes.
    /// </summary>
    /// <param name="routes">The Services.</param>
    /// <param name="serverId">This server's id.</param>
    /// <param name="send">Sends a request for a Service. Given the Service so its certificate rule can be used.</param>
    /// <param name="timeout">How long each target may take.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>The Services that point at this Jellyfin.</returns>
    public static async Task<List<ProxyRoute>> FindJellyfinTargetsAsync(
        IEnumerable<ProxyRoute> routes,
        string serverId,
        Func<ProxyRoute, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(send);
        var checks = routes
            .Where(r => r.Enabled && Uri.TryCreate(r.Target, UriKind.Absolute, out var t) && (t.Scheme == Uri.UriSchemeHttp || t.Scheme == Uri.UriSchemeHttps))
            .Select(async r => (Route: r, Self: await AnswersAsAsync(r, serverId, send, timeout, cancellationToken).ConfigureAwait(false)))
            .ToList();
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(r => r.Self).Select(r => r.Route).ToList();
    }

    private static async Task<bool> AnswersAsAsync(ProxyRoute route, string serverId, Func<ProxyRoute, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var target = new Uri(route.Target);
        var info = new UriBuilder(target) { Path = target.AbsolutePath.TrimEnd('/') + "/System/Info/Public", Query = string.Empty, Fragment = string.Empty }.Uri;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info);
            using var response = await send(route, request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (body.Length > 64 * 1024)
            {
                return false;
            }

            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("Id", out var id)
                && id.ValueKind == JsonValueKind.String
                && SameId(id.GetString(), serverId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not reachable now, or not Jellyfin: either way not this server.
            return false;
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
