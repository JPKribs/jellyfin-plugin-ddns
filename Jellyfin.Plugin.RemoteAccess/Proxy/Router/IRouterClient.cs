using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// One way of asking the router to forward a port, such as NAT-PMP or UPnP. Behind an interface so the port
/// mapper's decisions can be tested without a network.
/// </summary>
public interface IRouterClient
{
    /// <summary>Gets the name the dashboard shows for this way, such as "UPnP".</summary>
    string Method { get; }

    /// <summary>
    /// Gets the address of the device that answered <see cref="DiscoverAsync"/>, null before one did. Any device on
    /// the network can answer a UPnP search, so the dashboard names it for the person to check.
    /// </summary>
    IPAddress? Device { get; }

    /// <summary>
    /// Looks for a router that speaks this protocol on the network the LAN address is on.
    /// </summary>
    /// <param name="lanAddress">This machine's LAN address.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Whether a router answered. Never throws for a network failure.</returns>
    Task<bool> DiscoverAsync(IPAddress lanAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the router found by <see cref="DiscoverAsync"/> for its own internet address.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The address, or null when the router did not say.</returns>
    Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the router to forward a port, or to renew a forward it already has.
    /// </summary>
    /// <param name="mapping">The forward.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the router said. Never throws for a network failure.</returns>
    Task<PortMapResult> AddAsync(PortMapping mapping, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the router to stop forwarding a port. Best effort: never throws for a network failure.
    /// </summary>
    /// <param name="mapping">The forward.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(PortMapping mapping, CancellationToken cancellationToken);
}

/// <summary>
/// One TCP port forward on the router.
/// </summary>
/// <param name="ExternalPort">The port the internet connects to.</param>
/// <param name="InternalPort">The port the proxy listens on.</param>
/// <param name="LanAddress">This machine's LAN address.</param>
/// <param name="LifetimeSeconds">How long the router keeps the forward without a renewal.</param>
public sealed record PortMapping(int ExternalPort, int InternalPort, IPAddress LanAddress, int LifetimeSeconds)
{
    /// <summary>The description routers show next to the forward.</summary>
    public const string Description = "Jellyfin Remote Access";
}

/// <summary>
/// How a router answered a request to forward a port.
/// </summary>
public enum PortMapOutcome
{
    /// <summary>The port is forwarded.</summary>
    Mapped,

    /// <summary>The router already forwards the port to another device.</summary>
    Conflict,

    /// <summary>The router refused for another reason, or did not answer.</summary>
    Failed
}

/// <summary>
/// The router's answer to one forward.
/// </summary>
/// <param name="Outcome">Whether it worked.</param>
/// <param name="Error">Why it did not, as a plain sentence; empty when it did.</param>
public sealed record PortMapResult(PortMapOutcome Outcome, string Error)
{
    /// <summary>Gets the answer for a forward that worked.</summary>
    public static PortMapResult Ok { get; } = new(PortMapOutcome.Mapped, string.Empty);

    /// <summary>
    /// Builds the answer for a port another device holds.
    /// </summary>
    /// <param name="port">The external port.</param>
    /// <returns>The answer.</returns>
    public static PortMapResult Taken(int port) => new(PortMapOutcome.Conflict, $"The router already forwards port {port} to another device.");

    /// <summary>
    /// Builds the answer for any other refusal.
    /// </summary>
    /// <param name="error">Why, as a plain sentence.</param>
    /// <returns>The answer.</returns>
    public static PortMapResult Fail(string error) => new(PortMapOutcome.Failed, error);
}
