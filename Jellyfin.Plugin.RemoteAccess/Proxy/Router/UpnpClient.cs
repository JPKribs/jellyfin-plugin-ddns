using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Forwards ports with UPnP Internet Gateway Device: an SSDP search finds the router, its description names the
/// WAN connection service, and SOAP calls to that service add and delete forwards.
/// </summary>
public sealed class UpnpClient : IRouterClient, IDisposable
{
    // How long routers get to answer the search. The search asks for an answer within 2 seconds.
    private static readonly TimeSpan _searchWait = TimeSpan.FromMilliseconds(2500);

    // Descriptions and control answers are small. Anything larger is not a router talking.
    private const int MaxResponseBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Func<IPAddress, CancellationToken, Task<IReadOnlyList<(IPAddress From, string Reply)>>> _search;
    private (string ServiceType, Uri ControlUrl)? _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpnpClient"/> class that searches the real network.
    /// </summary>
    public UpnpClient()
        : this(
            new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) },
            SearchAsync)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpnpClient"/> class with a stand-in network, for tests.
    /// </summary>
    /// <param name="handler">Answers the description and control requests.</param>
    /// <param name="search">Runs the SSDP search and returns each reply with who sent it.</param>
    internal UpnpClient(HttpMessageHandler handler, Func<IPAddress, CancellationToken, Task<IReadOnlyList<(IPAddress From, string Reply)>>> search)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = MaxResponseBytes };
        _search = search;
    }

    /// <inheritdoc />
    public string Method => "UPnP";

    /// <inheritdoc />
    public async Task<bool> DiscoverAsync(IPAddress lanAddress, CancellationToken cancellationToken)
    {
        _service = null;
        IReadOnlyList<(IPAddress From, string Reply)> replies;
        try
        {
            replies = await _search(lanAddress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }

        var locations = replies
            .Select(r => (r.From, Parsed: UpnpProtocol.ParseSearchReply(r.Reply)))
            .Where(r => r.Parsed is not null)

            // A reply only counts for the device that sent it, so another device cannot steer this machine's
            // control calls elsewhere.
            .Where(r => IPAddress.TryParse(r.Parsed!.Value.Location.Host, out var host) && host.Equals(r.From))
            .Select(r => r.Parsed!.Value.Location)
            .Distinct()
            .ToList();
        foreach (var location in locations)
        {
            var xml = await GetTextAsync(location, cancellationToken).ConfigureAwait(false);
            if (xml is not null && UpnpProtocol.FindControl(xml, location) is { } service)
            {
                _service = service;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken)
    {
        var (ok, body) = await CallAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), cancellationToken).ConfigureAwait(false);
        return ok && IPAddress.TryParse(UpnpProtocol.ReadValue(body, "NewExternalIPAddress"), out var address) ? address : null;
    }

    /// <inheritdoc />
    public async Task<PortMapResult> AddAsync(PortMapping mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var lifetime = mapping.LifetimeSeconds;
        var freed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (ok, body) = await CallAsync("AddPortMapping", UpnpProtocol.AddArguments(mapping, lifetime), cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return PortMapResult.Ok;
            }

            var fault = UpnpProtocol.ReadFault(body);
            switch (fault?.Code)
            {
                // Some routers only keep forwards with no end. Those are removed when the proxy stops.
                case UpnpProtocol.OnlyPermanentLeasesSupported when lifetime != 0:
                    lifetime = 0;
                    continue;

                // The port is held. When it is this machine's own forward from an earlier run, such as one to an
                // old internal port, it is replaced. Another device's is never touched.
                case UpnpProtocol.ConflictInMappingEntry when !freed && await HeldByAsync(mapping, cancellationToken).ConfigureAwait(false):
                    await DeleteAsync(mapping, cancellationToken).ConfigureAwait(false);
                    freed = true;
                    continue;
                case { } code:
                    return code == UpnpProtocol.ConflictInMappingEntry
                        ? PortMapResult.Taken(mapping.ExternalPort)
                        : PortMapResult.Fail(UpnpProtocol.Describe(code, mapping.ExternalPort));
                default:
                    return PortMapResult.Fail(body.Length == 0
                        ? $"The router did not answer the request to open port {mapping.ExternalPort}."
                        : $"The router could not open port {mapping.ExternalPort}.");
            }
        }

        return PortMapResult.Fail($"The router could not open port {mapping.ExternalPort}.");
    }

    /// <inheritdoc />
    public async Task DeleteAsync(PortMapping mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        await CallAsync("DeletePortMapping", UpnpProtocol.PortArguments(mapping.ExternalPort), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    // Whether the router forwards the port to this machine already.
    private async Task<bool> HeldByAsync(PortMapping mapping, CancellationToken cancellationToken)
    {
        var (ok, body) = await CallAsync("GetSpecificPortMappingEntry", UpnpProtocol.PortArguments(mapping.ExternalPort), cancellationToken).ConfigureAwait(false);
        return ok && IPAddress.TryParse(UpnpProtocol.ReadValue(body, "NewInternalClient"), out var client) && client.Equals(mapping.LanAddress);
    }

    // A SOAP call to the service found by the search. A fault comes back as not ok with its body, so the caller
    // can read the UPnP error. A network failure is not ok with an empty body.
    private async Task<(bool Ok, string Body)> CallAsync(string action, IEnumerable<(string Name, string Value)> arguments, CancellationToken cancellationToken)
    {
        if (_service is not { } service)
        {
            return (false, string.Empty);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl)
        {
            Content = new StringContent(UpnpProtocol.Envelope(service.ServiceType, action, arguments), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + service.ServiceType + "#" + action + "\"");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            return (false, string.Empty);
        }
    }

    private async Task<string?> GetTextAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(location, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Sends one search per service type from the LAN address and collects replies for a moment.
    private static async Task<IReadOnlyList<(IPAddress From, string Reply)>> SearchAsync(IPAddress lanAddress, CancellationToken cancellationToken)
    {
        var replies = new List<(IPAddress, string)>();
        using var udp = new UdpClient(new IPEndPoint(lanAddress, 0));
        var targets = UpnpProtocol.ServiceTypes.Append("urn:schemas-upnp-org:device:InternetGatewayDevice:1");
        foreach (var target in targets)
        {
            var message = UpnpProtocol.SearchRequest(target);
            await udp.SendAsync(message, UpnpProtocol.SsdpEndpoint, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_searchWait);
        try
        {
            // Bounded so a noisy network cannot keep this collecting.
            while (replies.Count < 32)
            {
                var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                replies.Add((reply.RemoteEndPoint.Address, Encoding.ASCII.GetString(reply.Buffer)));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The wait is over. Whatever answered by now is what there is.
        }

        return replies;
    }
}
