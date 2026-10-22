using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Forwards ports with NAT-PMP (RFC 6886): small UDP messages to the default gateway on port 5351. Apple routers
/// and many others answer it, and it is quick to rule out when nothing does, so it is tried before UPnP.
/// </summary>
public sealed class NatPmpClient : IRouterClient
{
    /// <summary>The port NAT-PMP routers listen on.</summary>
    public const int Port = 5351;

    // RFC 6886 starts at 250 ms and doubles. Three tries keep a router that never answers to under two seconds.
    private static readonly TimeSpan[] _waits = { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) };

    private readonly Func<IPAddress, IPAddress?> _findGateway;
    private readonly Func<IPEndPoint, byte[], CancellationToken, Task<byte[]?>> _send;
    private IPAddress? _gateway;
    private IPAddress? _external;

    /// <summary>
    /// Initializes a new instance of the <see cref="NatPmpClient"/> class that talks to the real gateway.
    /// </summary>
    public NatPmpClient()
        : this(FindGateway, SendUdpAsync)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NatPmpClient"/> class with a stand-in network, for tests.
    /// </summary>
    /// <param name="findGateway">Finds the gateway for a LAN address.</param>
    /// <param name="send">Sends one request and returns the reply, or null when none came.</param>
    internal NatPmpClient(Func<IPAddress, IPAddress?> findGateway, Func<IPEndPoint, byte[], CancellationToken, Task<byte[]?>> send)
    {
        _findGateway = findGateway;
        _send = send;
    }

    /// <inheritdoc />
    public string Method => "NAT-PMP";

    /// <inheritdoc />
    public IPAddress? Device => _external is null ? null : _gateway;

    /// <inheritdoc />
    public async Task<bool> DiscoverAsync(IPAddress lanAddress, CancellationToken cancellationToken)
    {
        _gateway = _findGateway(lanAddress);
        _external = null;
        if (_gateway is null)
        {
            return false;
        }

        // The public address request is the protocol's own "are you there". A router that answers with an error
        // code still speaks NAT-PMP, but one that refuses this will refuse mappings too.
        var reply = await SendAsync(NatPmpPacket.PublicAddressRequest(), cancellationToken).ConfigureAwait(false);
        if (reply is null || NatPmpPacket.ParsePublicAddress(reply) is not { } answer || answer.ResultCode != 0)
        {
            return false;
        }

        _external = answer.Address;
        return true;
    }

    /// <inheritdoc />
    public Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken) => Task.FromResult(_external);

    /// <inheritdoc />
    public async Task<PortMapResult> AddAsync(PortMapping mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var reply = await SendAsync(NatPmpPacket.MapTcpRequest(mapping.InternalPort, mapping.ExternalPort, (uint)mapping.LifetimeSeconds), cancellationToken).ConfigureAwait(false);
        if (reply is null || NatPmpPacket.ParseMap(reply) is not { } answer)
        {
            return PortMapResult.Fail($"The router did not answer the request to open port {mapping.ExternalPort}.");
        }

        if (answer.ResultCode != 0)
        {
            return PortMapResult.Fail(NatPmpPacket.Describe(answer.ResultCode, mapping.ExternalPort));
        }

        // A router gives another external port when the one asked for is taken. A forward on some other port is
        // of no use, since the internet always comes in on 80 and 443, so it is handed back at once.
        if (answer.ExternalPort != mapping.ExternalPort)
        {
            await SendAsync(NatPmpPacket.MapTcpRequest(mapping.InternalPort, 0, 0), cancellationToken).ConfigureAwait(false);
            return PortMapResult.Taken(mapping.ExternalPort);
        }

        return PortMapResult.Ok;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(PortMapping mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        // RFC 6886 deletes a mapping by asking for it again with a lifetime and external port of zero.
        await SendAsync(NatPmpPacket.MapTcpRequest(mapping.InternalPort, 0, 0), cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (_gateway is null)
        {
            return null;
        }

        try
        {
            return await _send(new IPEndPoint(_gateway, Port), request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    // The gateway of the interface that holds the LAN address, which is the router NAT-PMP talks to.
    internal static IPAddress? FindGateway(IPAddress lanAddress)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetIPProperties())
                .Where(p => p.UnicastAddresses.Any(u => u.Address.Equals(lanAddress)))
                .SelectMany(p => p.GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> SendUdpAsync(IPEndPoint gateway, byte[] request, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        foreach (var wait in _waits)
        {
            await udp.SendAsync(request, gateway, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                    // Only the gateway's own answer counts. Anything else on the socket is ignored.
                    if (reply.RemoteEndPoint.Address.Equals(gateway.Address))
                    {
                        return reply.Buffer;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No answer in time: send again with a longer wait.
            }
        }

        return null;
    }
}

/// <summary>
/// Builds and reads NAT-PMP messages. Every field is big-endian.
/// </summary>
internal static class NatPmpPacket
{
    /// <summary>Builds the public address request: version 0, opcode 0.</summary>
    /// <returns>The request.</returns>
    public static byte[] PublicAddressRequest() => new byte[] { 0, 0 };

    /// <summary>
    /// Builds a TCP mapping request: version 0, opcode 2, two reserved bytes, the internal port, the suggested
    /// external port and the lifetime in seconds.
    /// </summary>
    /// <param name="internalPort">The port on this machine.</param>
    /// <param name="externalPort">The port asked for on the router, 0 when deleting.</param>
    /// <param name="lifetime">The lifetime in seconds, 0 to delete.</param>
    /// <returns>The request.</returns>
    public static byte[] MapTcpRequest(int internalPort, int externalPort, uint lifetime)
    {
        var packet = new byte[12];
        packet[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), lifetime);
        return packet;
    }

    /// <summary>
    /// Reads a public address response: version, opcode 128, result code, seconds since the router started, and
    /// the address.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>The result code and address, or null when the reply is not one.</returns>
    public static (int ResultCode, IPAddress Address)? ParsePublicAddress(byte[] reply)
    {
        if (reply is null || reply.Length < 12 || reply[0] != 0 || reply[1] != 128)
        {
            return null;
        }

        return (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)), new IPAddress(reply.AsSpan(8, 4)));
    }

    /// <summary>
    /// Reads a TCP mapping response: version, opcode 130, result code, seconds since the router started, the
    /// internal port, the external port the router gave and the lifetime it granted.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <returns>The fields, or null when the reply is not one.</returns>
    public static (int ResultCode, int InternalPort, int ExternalPort, uint Lifetime)? ParseMap(byte[] reply)
    {
        if (reply is null || reply.Length < 16 || reply[0] != 0 || reply[1] != 130)
        {
            return null;
        }

        return (
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)),
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)),
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10)),
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12)));
    }

    /// <summary>
    /// Says what a NAT-PMP result code means for the administrator.
    /// </summary>
    /// <param name="resultCode">The code.</param>
    /// <param name="port">The external port asked for.</param>
    /// <returns>A plain sentence.</returns>
    public static string Describe(int resultCode, int port) => resultCode switch
    {
        2 => "The router does not allow this server to open ports. Turn on NAT-PMP or UPnP for it in the router's settings.",
        3 => "The router is not connected to the internet right now.",
        4 => "The router has no room left for more forwarded ports.",
        _ => $"The router could not open port {port}."
    };
}
