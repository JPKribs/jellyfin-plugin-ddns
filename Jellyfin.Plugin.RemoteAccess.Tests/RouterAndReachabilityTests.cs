using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Router;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Plex-style remote access: forwarding ports on the router with NAT-PMP and UPnP, telling a provider's shared
/// address apart, checking the public name, and the outside reachability check. No test touches the network.
/// </summary>
public class RouterAndReachabilityTests
{
    private static readonly IPAddress _lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress _gateway = IPAddress.Parse("192.168.1.1");

    // ---- NAT-PMP packets ----

    [Fact]
    public void NatPmp_PublicAddressRequestIsVersionAndOpcodeZero()
        => Assert.Equal(new byte[] { 0, 0 }, NatPmpPacket.PublicAddressRequest());

    [Fact]
    public void NatPmp_MapRequestCarriesPortsAndLifetimeBigEndian()
    {
        var packet = NatPmpPacket.MapTcpRequest(8443, 443, 7200);
        Assert.Equal(new byte[] { 0, 2, 0, 0, 0x20, 0xFB, 0x01, 0xBB, 0, 0, 0x1C, 0x20 }, packet);
    }

    [Fact]
    public void NatPmp_ReadsACapturedPublicAddressReply()
    {
        // Captured from an AirPort: version 0, opcode 128, result 0, 1 234 567 s since boot, 203.0.113.7.
        var reply = new byte[] { 0, 128, 0, 0, 0x00, 0x12, 0xD6, 0x87, 203, 0, 113, 7 };
        var parsed = NatPmpPacket.ParsePublicAddress(reply);
        Assert.NotNull(parsed);
        Assert.Equal(0, parsed!.Value.ResultCode);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), parsed.Value.Address);
    }

    [Theory]
    [InlineData(new byte[] { 0, 128, 0, 0 })]
    [InlineData(new byte[] { 0, 130, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 })]
    [InlineData(new byte[] { 1, 128, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 })]
    public void NatPmp_RejectsWhatIsNotAPublicAddressReply(byte[] reply)
        => Assert.Null(NatPmpPacket.ParsePublicAddress(reply));

    [Fact]
    public void NatPmp_ReadsACapturedMapReply()
    {
        var reply = new byte[] { 0, 130, 0, 0, 0x00, 0x12, 0xD6, 0x88, 0x20, 0xFB, 0x01, 0xBB, 0, 0, 0x1C, 0x20 };
        var parsed = NatPmpPacket.ParseMap(reply);
        Assert.NotNull(parsed);
        Assert.Equal((0, 8443, 443, 7200u), parsed!.Value);
    }

    [Fact]
    public void NatPmp_ResultCodesAreExplainedPlainly()
    {
        Assert.Contains("does not allow", NatPmpPacket.Describe(2, 443), StringComparison.Ordinal);
        Assert.Equal("The router could not open port 443.", NatPmpPacket.Describe(5, 443));
    }

    [Fact]
    public async Task NatPmpClient_MapsWhenTheRouterGivesThePortAskedFor()
    {
        var sent = new List<byte[]>();
        var client = new NatPmpClient(_ => _gateway, (to, request, _) =>
        {
            Assert.Equal(new IPEndPoint(_gateway, NatPmpClient.Port), to);
            sent.Add(request);
            return Task.FromResult<byte[]?>(request[1] == 0
                ? new byte[] { 0, 128, 0, 0, 0, 0, 0, 1, 203, 0, 113, 7 }
                : MapReply(0, request, external: null));
        });

        Assert.True(await client.DiscoverAsync(_lan, CancellationToken.None));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), await client.GetExternalAddressAsync(CancellationToken.None));
        var result = await client.AddAsync(new PortMapping(443, 8443, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Mapped, result.Outcome);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task NatPmpClient_AnotherExternalPortMeansThePortIsTakenAndIsHandedBack()
    {
        var sent = new List<byte[]>();
        var client = new NatPmpClient(_ => _gateway, (_, request, _) =>
        {
            sent.Add(request);
            return Task.FromResult<byte[]?>(request[1] == 0
                ? new byte[] { 0, 128, 0, 0, 0, 0, 0, 1, 203, 0, 113, 7 }
                : MapReply(0, request, external: 50443));
        });

        await client.DiscoverAsync(_lan, CancellationToken.None);
        var result = await client.AddAsync(new PortMapping(443, 8443, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Conflict, result.Outcome);
        Assert.Equal("The router already forwards port 443 to another device.", result.Error);

        // The forward on the other port is deleted: lifetime and external port zero.
        Assert.Equal(NatPmpPacket.MapTcpRequest(8443, 0, 0), sent[^1]);
    }

    [Fact]
    public async Task NatPmpClient_RefusalIsExplained()
    {
        var client = new NatPmpClient(_ => _gateway, (_, request, _) => Task.FromResult<byte[]?>(request[1] == 0
            ? new byte[] { 0, 128, 0, 0, 0, 0, 0, 1, 203, 0, 113, 7 }
            : MapReply(2, request, external: null)));
        await client.DiscoverAsync(_lan, CancellationToken.None);

        var result = await client.AddAsync(new PortMapping(80, 80, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Failed, result.Outcome);
        Assert.StartsWith("The router does not allow this server to open ports.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NatPmpClient_NoGatewayOrNoAnswerIsNotFound()
    {
        Assert.False(await new NatPmpClient(_ => null, (_, _, _) => Task.FromResult<byte[]?>(null)).DiscoverAsync(_lan, CancellationToken.None));
        Assert.False(await new NatPmpClient(_ => _gateway, (_, _, _) => Task.FromResult<byte[]?>(null)).DiscoverAsync(_lan, CancellationToken.None));
    }

    private static byte[] MapReply(int code, byte[] request, int? external)
    {
        var reply = new byte[16];
        reply[1] = 130;
        reply[3] = (byte)code;
        reply[8] = request[4];
        reply[9] = request[5];
        var port = external ?? ((request[6] << 8) | request[7]);
        reply[10] = (byte)(port >> 8);
        reply[11] = (byte)port;
        Array.Copy(request, 8, reply, 12, 4);
        return reply;
    }

    // ---- UPnP messages ----

    private const string SearchReply =
        "HTTP/1.1 200 OK\r\n"
        + "CACHE-CONTROL: max-age=120\r\n"
        + "ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n"
        + "USN: uuid:3d3cec3a-8cf0-11e0-98ee-001a6bd2d07b::urn:schemas-upnp-org:service:WANIPConnection:1\r\n"
        + "EXT:\r\n"
        + "SERVER: AsusWRT/386 UPnP/1.1 MiniUPnPd/2.2.0\r\n"
        + "Location: http://192.168.1.1:5000/rootDesc.xml\r\n"
        + "\r\n";

    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>1</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>RT-AX88U</friendlyName>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/ctl/L3F</controlURL>
              </service>
            </serviceList>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                        <controlURL>/ctl/IPConn</controlURL>
                        <eventSubURL>/evt/IPConn</eventSubURL>
                        <SCPDURL>/WANIPCn.xml</SCPDURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    private const string ExternalAddressResponse = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
              <NewExternalIPAddress>100.71.2.3</NewExternalIPAddress>
            </u:GetExternalIPAddressResponse>
          </s:Body>
        </s:Envelope>
        """;

    private static string Fault(int code, string description) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>{code}</errorCode>
                  <errorDescription>{description}</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    private static string EntryResponse(string client) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:GetSpecificPortMappingEntryResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
              <NewInternalPort>8443</NewInternalPort>
              <NewInternalClient>{client}</NewInternalClient>
              <NewEnabled>1</NewEnabled>
              <NewPortMappingDescription>NAS</NewPortMappingDescription>
              <NewLeaseDuration>0</NewLeaseDuration>
            </u:GetSpecificPortMappingEntryResponse>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void Upnp_ReadsACapturedSearchReply()
    {
        var parsed = UpnpProtocol.ParseSearchReply(SearchReply);
        Assert.NotNull(parsed);
        Assert.Equal(new Uri("http://192.168.1.1:5000/rootDesc.xml"), parsed!.Value.Location);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", parsed.Value.SearchTarget);
    }

    [Fact]
    public void Upnp_IgnoresRepliesForOtherDevicesAndNotifications()
    {
        Assert.Null(UpnpProtocol.ParseSearchReply(SearchReply.Replace("service:WANIPConnection:1\r\nUSN", "device:MediaRenderer:1\r\nUSN", StringComparison.Ordinal)));
        Assert.Null(UpnpProtocol.ParseSearchReply("NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.1/x.xml\r\nNT: upnp:rootdevice\r\n\r\n"));
        Assert.Null(UpnpProtocol.ParseSearchReply(SearchReply.Replace("http://192.168.1.1:5000", "https://192.168.1.1:5000", StringComparison.Ordinal)));
        Assert.Null(UpnpProtocol.ParseSearchReply(string.Empty));
    }

    [Fact]
    public void Upnp_SearchAsksForTheService()
    {
        var text = Encoding.ASCII.GetString(UpnpProtocol.SearchRequest("urn:schemas-upnp-org:service:WANIPConnection:1"));
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", text, StringComparison.Ordinal);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Upnp_FindsTheNestedControlUrl()
    {
        var found = UpnpProtocol.FindControl(Description, new Uri("http://192.168.1.1:5000/rootDesc.xml"));
        Assert.NotNull(found);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", found!.Value.ServiceType);
        Assert.Equal(new Uri("http://192.168.1.1:5000/ctl/IPConn"), found.Value.ControlUrl);
    }

    [Fact]
    public void Upnp_UsesUrlBaseAndPrefersVersionTwo()
    {
        var xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <URLBase>http://192.168.1.1:49000/</URLBase>
              <device><deviceList><device><serviceList>
                <service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>upnp/control/WANPPP</controlURL></service>
                <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType><controlURL>upnp/control/WANIP2</controlURL></service>
              </serviceList></device></deviceList></device>
            </root>
            """;
        var found = UpnpProtocol.FindControl(xml, new Uri("http://192.168.1.1:49000/igddesc.xml"));
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:2", found!.Value.ServiceType);
        Assert.Equal(new Uri("http://192.168.1.1:49000/upnp/control/WANIP2"), found.Value.ControlUrl);

        var ppp = xml.Replace("<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType><controlURL>upnp/control/WANIP2</controlURL></service>", string.Empty, StringComparison.Ordinal);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", UpnpProtocol.FindControl(ppp, new Uri("http://192.168.1.1:49000/igddesc.xml"))!.Value.ServiceType);
    }

    [Fact]
    public void Upnp_ControlUrlOnAnotherHostIsRefused()
    {
        var xml = Description.Replace("<controlURL>/ctl/IPConn</controlURL>", "<controlURL>http://198.51.100.9/ctl</controlURL>", StringComparison.Ordinal);
        Assert.Null(UpnpProtocol.FindControl(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml")));
    }

    [Fact]
    public void Upnp_DescriptionWithADtdOrNoServiceIsIgnored()
    {
        var location = new Uri("http://192.168.1.1:5000/rootDesc.xml");
        Assert.Null(UpnpProtocol.FindControl("<!DOCTYPE x [<!ENTITY a \"b\">]><root>&a;</root>", location));
        Assert.Null(UpnpProtocol.FindControl("<root><device/></root>", location));
        Assert.Null(UpnpProtocol.FindControl("not xml", location));
    }

    [Fact]
    public void Upnp_EnvelopeCarriesTheArgumentsInOrder()
    {
        var envelope = UpnpProtocol.Envelope(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "AddPortMapping",
            UpnpProtocol.AddArguments(new PortMapping(443, 8443, _lan, 7200), 7200));

        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", envelope, StringComparison.Ordinal);
        Assert.Contains(
            "<NewRemoteHost></NewRemoteHost><NewExternalPort>443</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>8443</NewInternalPort>"
            + "<NewInternalClient>192.168.1.20</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>Jellyfin Remote Access</NewPortMappingDescription>"
            + "<NewLeaseDuration>7200</NewLeaseDuration>",
            envelope,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Upnp_ReadsTheExternalAddressAndFaults()
    {
        Assert.Equal("100.71.2.3", UpnpProtocol.ReadValue(ExternalAddressResponse, "NewExternalIPAddress"));
        Assert.Equal((718, "ConflictInMappingEntry"), UpnpProtocol.ReadFault(Fault(718, "ConflictInMappingEntry")));
        Assert.Null(UpnpProtocol.ReadFault(ExternalAddressResponse));
        Assert.Equal("The router already forwards port 443 to another device.", UpnpProtocol.Describe(718, 443));
        Assert.StartsWith("The router does not allow this server to open ports.", UpnpProtocol.Describe(606, 443), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpnpClient_DiscoversAndReadsTheExternalAddress()
    {
        using var client = Upnp(_ => (HttpStatusCode.OK, ExternalAddressResponse));
        Assert.True(await client.DiscoverAsync(_lan, CancellationToken.None));
        Assert.Equal(IPAddress.Parse("100.71.2.3"), await client.GetExternalAddressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpnpClient_ReplyNamingAnotherHostIsIgnored()
    {
        using var client = Upnp(_ => (HttpStatusCode.OK, string.Empty), from: IPAddress.Parse("192.168.1.66"));
        Assert.False(await client.DiscoverAsync(_lan, CancellationToken.None));
    }

    [Fact]
    public async Task UpnpClient_ConflictWithAnotherDeviceIsReportedAndLeftAlone()
    {
        var actions = new List<string>();
        using var client = Upnp(action =>
        {
            actions.Add(action);
            return action switch
            {
                "AddPortMapping" => (HttpStatusCode.InternalServerError, Fault(718, "ConflictInMappingEntry")),
                "GetSpecificPortMappingEntry" => (HttpStatusCode.OK, EntryResponse("192.168.1.50")),
                _ => (HttpStatusCode.OK, string.Empty)
            };
        });
        await client.DiscoverAsync(_lan, CancellationToken.None);

        var result = await client.AddAsync(new PortMapping(443, 8443, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Conflict, result.Outcome);
        Assert.Equal("The router already forwards port 443 to another device.", result.Error);
        Assert.DoesNotContain("DeletePortMapping", actions);
    }

    [Fact]
    public async Task UpnpClient_OwnOldForwardIsReplaced()
    {
        var actions = new List<string>();
        var adds = 0;
        using var client = Upnp(action =>
        {
            actions.Add(action);
            return action switch
            {
                "AddPortMapping" => ++adds == 1 ? (HttpStatusCode.InternalServerError, Fault(718, "ConflictInMappingEntry")) : (HttpStatusCode.OK, string.Empty),
                "GetSpecificPortMappingEntry" => (HttpStatusCode.OK, EntryResponse("192.168.1.20")),
                _ => (HttpStatusCode.OK, string.Empty)
            };
        });
        await client.DiscoverAsync(_lan, CancellationToken.None);

        var result = await client.AddAsync(new PortMapping(443, 8443, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Mapped, result.Outcome);
        Assert.Equal(new[] { "AddPortMapping", "GetSpecificPortMappingEntry", "DeletePortMapping", "AddPortMapping" }, actions);
    }

    [Fact]
    public async Task UpnpClient_RouterThatOnlyKeepsPermanentForwardsGetsOne()
    {
        var bodies = new List<string>();
        using var client = Upnp(
            action => bodies.Count == 1 ? (HttpStatusCode.InternalServerError, Fault(725, "OnlyPermanentLeasesSupported")) : (HttpStatusCode.OK, string.Empty),
            bodies);
        await client.DiscoverAsync(_lan, CancellationToken.None);

        var result = await client.AddAsync(new PortMapping(80, 8080, _lan, 7200), CancellationToken.None);

        Assert.Equal(PortMapOutcome.Mapped, result.Outcome);
        Assert.Contains("<NewLeaseDuration>7200</NewLeaseDuration>", bodies[0], StringComparison.Ordinal);
        Assert.Contains("<NewLeaseDuration>0</NewLeaseDuration>", bodies[1], StringComparison.Ordinal);
    }

    // A UPnP client whose search finds the router at 192.168.1.1 and whose description and control calls are
    // answered here. Control calls are answered by their SOAPAction.
    private static UpnpClient Upnp(Func<string, (HttpStatusCode, string)> control, List<string>? bodies = null, IPAddress? from = null)
    {
        var handler = new RouterHandler(control, bodies ?? new List<string>());
        return new UpnpClient(handler, (_, _) => Task.FromResult<IReadOnlyList<(IPAddress, string)>>(new[] { (from ?? _gateway, SearchReply) }));
    }

    private sealed class RouterHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _control;
        private readonly List<string> _bodies;

        public RouterHandler(Func<string, (HttpStatusCode, string)> control, List<string> bodies)
        {
            _control = control;
            _bodies = bodies;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Description) };
            }

            _bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var soapAction = request.Headers.GetValues("SOAPAction").Single().Trim('"');
            var (code, body) = _control(soapAction[(soapAction.IndexOf('#', StringComparison.Ordinal) + 1)..]);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    // ---- The port mapper's decisions ----

    private sealed class FakeRouter : IRouterClient
    {
        public FakeRouter(string method) => Method = method;

        public string Method { get; }

        public bool Answers { get; set; } = true;

        public IPAddress? External { get; set; } = IPAddress.Parse("203.0.113.7");

        public Dictionary<int, PortMapResult> Results { get; } = new();

        public int Discovers { get; private set; }

        public List<PortMapping> Added { get; } = new();

        public List<PortMapping> Deleted { get; } = new();

        public Task<bool> DiscoverAsync(IPAddress lanAddress, CancellationToken cancellationToken)
        {
            Discovers++;
            return Task.FromResult(Answers);
        }

        public Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken) => Task.FromResult(External);

        public Task<PortMapResult> AddAsync(PortMapping mapping, CancellationToken cancellationToken)
        {
            Added.Add(mapping);
            return Task.FromResult(Results.GetValueOrDefault(mapping.ExternalPort, PortMapResult.Ok));
        }

        public Task DeleteAsync(PortMapping mapping, CancellationToken cancellationToken)
        {
            Deleted.Add(mapping);
            return Task.CompletedTask;
        }
    }

    private static readonly RouterRequest _both = new(true, _lan, new[] { (80, 8080), (443, 8443) });

    private static (RouterPortMapper Mapper, List<ActivityLog> Entries) Mapper(params IRouterClient[] clients)
    {
        var entries = new List<ActivityLog>();
        var manager = Substitute.For<IActivityManager>();
        manager.CreateAsync(Arg.Do<ActivityLog>(entries.Add));
        var activity = new ActivityLogger(manager, Substitute.For<ILogger<ActivityLogger>>());
        return (new RouterPortMapper(clients, NullLogger.Instance, activity), entries);
    }

    private static async Task Sync(RouterPortMapper mapper, RouterRequest request, bool force = false)
    {
        mapper.Desire(request);
        await mapper.SyncAsync(force, CancellationToken.None);
    }

    [Fact]
    public async Task Mapper_TriesNatPmpFirst()
    {
        var natpmp = new FakeRouter("NAT-PMP");
        var upnp = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(natpmp, upnp);
        await using var _ = mapper;

        await Sync(mapper, _both);

        var status = mapper.Status;
        Assert.Equal("NAT-PMP", status.Method);
        Assert.True(status.Mapped);
        Assert.Equal(new[] { 80, 443 }, status.Ports);
        Assert.Equal("192.168.1.20", status.LanAddress);
        Assert.Equal("203.0.113.7", status.ExternalAddress);
        Assert.NotNull(status.RenewedUtc);
        Assert.Equal(string.Empty, status.Error);
        Assert.Equal(0, upnp.Discovers);
        Assert.Equal(new[] { new PortMapping(80, 8080, _lan, RouterPortMapper.LeaseSeconds), new PortMapping(443, 8443, _lan, RouterPortMapper.LeaseSeconds) }, natpmp.Added);
    }

    [Fact]
    public async Task Mapper_FallsBackToUpnpWhenNatPmpDoesNotAnswer()
    {
        var natpmp = new FakeRouter("NAT-PMP") { Answers = false };
        var upnp = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(natpmp, upnp);
        await using var _ = mapper;

        await Sync(mapper, _both);

        Assert.Equal("UPnP", mapper.Status.Method);
        Assert.Equal(2, upnp.Added.Count);
        Assert.Empty(natpmp.Added);
    }

    [Fact]
    public async Task Mapper_FallsBackWhenTheFirstWayRefusesEverything()
    {
        var natpmp = new FakeRouter("NAT-PMP");
        natpmp.Results[80] = PortMapResult.Fail("The router could not open port 80.");
        natpmp.Results[443] = PortMapResult.Fail("The router could not open port 443.");
        var upnp = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(natpmp, upnp);
        await using var _ = mapper;

        await Sync(mapper, _both);

        Assert.Equal("UPnP", mapper.Status.Method);
        Assert.True(mapper.Status.Mapped);
    }

    [Fact]
    public async Task Mapper_MapsNothingWhenOff()
    {
        var router = new FakeRouter("NAT-PMP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, RouterRequest.Off, force: true);
        await Sync(mapper, new RouterRequest(true, _lan, Array.Empty<(int, int)>()), force: true);

        Assert.Equal(0, router.Discovers);
        Assert.Empty(router.Added);
        Assert.False(mapper.Status.Mapped);
    }

    [Fact]
    public async Task Mapper_RemovesTheForwardsWhenTurnedOffOrStopped()
    {
        var router = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        await Sync(mapper, RouterRequest.Off);

        Assert.Equal(new[] { 80, 443 }, router.Deleted.Select(m => m.ExternalPort));
        Assert.False(mapper.Status.Enabled);
        Assert.Empty(mapper.Status.Ports);

        await Sync(mapper, _both);
        await mapper.StopAsync(CancellationToken.None);
        Assert.Equal(4, router.Deleted.Count);
    }

    [Fact]
    public async Task Mapper_ProxyStoppingRemovesTheForwardsButStaysEnabled()
    {
        var router = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        await Sync(mapper, new RouterRequest(true, _lan, Array.Empty<(int, int)>()));

        Assert.Equal(2, router.Deleted.Count);
        Assert.True(mapper.Status.Enabled);
        Assert.Equal(string.Empty, mapper.Status.Error);
    }

    [Fact]
    public async Task Mapper_OnlyRenewsWhenAskedToOrSomethingChanged()
    {
        var router = new FakeRouter("NAT-PMP");
        var (mapper, entries) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        await Sync(mapper, _both);
        Assert.Equal(2, router.Added.Count);

        // The hourly renewal asks again with the full lease.
        await Sync(mapper, _both, force: true);
        Assert.Equal(4, router.Added.Count);
        Assert.All(router.Added, m => Assert.Equal(RouterPortMapper.LeaseSeconds, m.LifetimeSeconds));

        // The opening is one entry, not one per renewal.
        var opened = Assert.Single(entries);
        Assert.Equal("Remote Access opened ports 80 and 443 on your router", opened.Name);
        Assert.Equal("DynamicDNS.RouterPortsOpened", opened.Type);
        Assert.Equal(LogLevel.Information, opened.LogSeverity);
    }

    [Fact]
    public async Task Mapper_ChangedPortRemovesTheOldForwardFirst()
    {
        var router = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        await Sync(mapper, new RouterRequest(true, _lan, new[] { (80, 8080), (443, 9443) }));

        Assert.Equal(new PortMapping(443, 8443, _lan, RouterPortMapper.LeaseSeconds), Assert.Single(router.Deleted));
        Assert.Equal(9443, router.Added[^1].InternalPort);
    }

    [Fact]
    public async Task Mapper_RenewalFailureIsShownAndLoggedOnce()
    {
        var router = new FakeRouter("NAT-PMP");
        var (mapper, entries) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        router.Answers = false;
        await Sync(mapper, _both, force: true);
        await Sync(mapper, _both, force: true);

        var status = mapper.Status;
        Assert.False(status.Mapped);
        Assert.Equal(RouterPortMapper.NoRouterSentence(new[] { new PortMapping(80, 80, _lan, 1), new PortMapping(443, 443, _lan, 1) }), status.Error);
        var warnings = entries.Where(e => e.LogSeverity == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Equal("DynamicDNS.RouterNotFound", warning.Type);
        Assert.Equal(status.Error, warning.ShortOverview);
    }

    [Fact]
    public async Task Mapper_ConflictIsSaidPlainly()
    {
        var router = new FakeRouter("UPnP");
        router.Results[443] = PortMapResult.Taken(443);
        var other = new FakeRouter("Other");
        var (mapper, entries) = Mapper(router, other);
        await using var _ = mapper;

        await Sync(mapper, _both);

        var status = mapper.Status;
        Assert.Equal("The router already forwards port 443 to another device.", status.Error);
        Assert.Equal(new[] { 80 }, status.Ports);
        Assert.False(status.Mapped);
        Assert.Equal("UPnP", status.Method);
        Assert.Equal(0, other.Discovers);
        Assert.Contains(entries, e => e.Type == "DynamicDNS.RouterPortsFailed" && e.ShortOverview == status.Error);
        Assert.Contains(entries, e => e.Name == "Remote Access opened port 80 on your router");

        router.Results[80] = PortMapResult.Taken(80);
        await Sync(mapper, _both, force: true);
        Assert.Equal("The router already forwards ports 80 and 443 to another device.", mapper.Status.Error);
    }

    [Fact]
    public async Task Mapper_NoLanAddressIsExplained()
    {
        var router = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, new RouterRequest(true, null, new[] { (80, 80) }));

        Assert.Equal(0, router.Discovers);
        Assert.StartsWith("This server's LAN address could not be found.", mapper.Status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mapper_RetiredNeverMapsAgain()
    {
        var router = new FakeRouter("UPnP");
        var (mapper, _) = Mapper(router);
        await using var _ = mapper;

        await Sync(mapper, _both);
        mapper.Retire();
        await mapper.SyncAsync(true, CancellationToken.None);
        await Sync(mapper, _both, force: true);

        Assert.Equal(2, router.Added.Count);
        Assert.Equal(2, router.Deleted.Count);
    }

    [Fact]
    public void Request_FollowsTheSettingAndTheListener()
    {
        var settings = new ProxySettings { Enabled = true };
        var both = new ListenerOptions(8080, 8443, false, true, 130, 15, 32, 2000);
        var request = ProxyManager.RouterRequestFor(settings, both, _lan);
        Assert.True(request.Enabled);
        Assert.Equal(_lan, request.LanAddress);
        Assert.Equal(new[] { (80, 8080), (443, 8443) }, request.Ports);

        // HTTPS waits for a certificate, so only port 80 is forwarded until then.
        Assert.Equal(new[] { (80, 8080) }, ProxyManager.RouterRequestFor(settings, both with { HttpsPort = null }, _lan).Ports);
        Assert.Empty(ProxyManager.RouterRequestFor(settings, both with { LoopbackOnly = true }, _lan).Ports);
        Assert.Empty(ProxyManager.RouterRequestFor(settings, null, _lan).Ports);

        settings.Advanced = new ProxyAdvancedSettings { LanAddress = "192.168.1.77" };
        Assert.Equal(IPAddress.Parse("192.168.1.77"), ProxyManager.RouterRequestFor(settings, both, _lan).LanAddress);

        settings.Advanced = new ProxyAdvancedSettings { AutoPortForwarding = false };
        Assert.Same(RouterRequest.Off, ProxyManager.RouterRequestFor(settings, both, _lan));
        Assert.Same(RouterRequest.Off, ProxyManager.RouterRequestFor(new ProxySettings { Enabled = false }, both, _lan));
    }

    [Fact]
    public void Options_AutoPortForwardingIsOnByDefault()
    {
        Assert.True(ProxyOptions.Standard.AutoPortForwarding);
        Assert.True(ProxyOptions.LowMemory.AutoPortForwarding);
        Assert.True(ProxyOptions.From(new ProxyAdvancedSettings()).AutoPortForwarding);
        Assert.False(ProxyOptions.From(new ProxyAdvancedSettings { AutoPortForwarding = false }).AutoPortForwarding);
    }

    [Fact]
    public void PublicHostname_IsTheFirstExactPublicName()
    {
        Assert.Equal("media.example.com", ProxyManager.PublicHostname(new ProxySettings { Hostnames = new List<string> { "*.example.com", "jellyfin.lan", "media.example.com", "b.example.com" } }));
        Assert.Equal(string.Empty, ProxyManager.PublicHostname(new ProxySettings { Hostnames = new List<string> { "*.example.com", "nas.local" } }));
    }

    // ---- Shared address detection ----

    [Theory]
    [InlineData("100.71.2.3", "198.51.100.4", true)]
    [InlineData("100.64.0.1", "", true)]
    [InlineData("100.127.255.254", "", true)]
    [InlineData("100.128.0.1", "100.128.0.1", false)]
    [InlineData("100.63.255.255", "", false)]
    [InlineData("10.20.0.2", "", true)]
    [InlineData("172.16.5.1", "", true)]
    [InlineData("192.168.0.5", "", true)]
    [InlineData("203.0.113.7", "203.0.113.7", false)]
    [InlineData("203.0.113.7", "198.51.100.4", true)]
    [InlineData("203.0.113.7", "", false)]
    [InlineData("203.0.113.7", "2001:db8::1", false)]
    [InlineData("", "198.51.100.4", false)]
    [InlineData("0.0.0.0", "198.51.100.4", false)]
    public void Cgnat_IsToldFromTheRoutersAddress(string router, string seen, bool behind)
    {
        var (isBehind, reason) = CgnatCheck.Evaluate(router, seen);
        Assert.Equal(behind, isBehind);
        Assert.Equal(behind, reason.Length > 0);
        if (behind)
        {
            Assert.Contains(router, reason, StringComparison.Ordinal);
            AssertPlain(reason);
        }
    }

    [Fact]
    public void Cgnat_SharedAddressReasonReadsPlainly()
        => Assert.Equal(
            "Your router's internet address is 100.71.2.3, a shared address from your provider. Forwarded ports cannot be reached from the internet, so ask your provider for a public IP.",
            CgnatCheck.Evaluate("100.71.2.3", string.Empty).Reason);

    // ---- The name check ----

    [Fact]
    public void Name_ResolvesWhenAnAddressIsThisServers()
    {
        var v4 = NameChecker.Compare("media.example.com", new[] { IPAddress.Parse("203.0.113.7") }, "203.0.113.7", string.Empty);
        Assert.True(v4.Resolves);
        Assert.Equal("203.0.113.7", v4.PublicAddress);
        Assert.Equal(string.Empty, v4.Error);

        var v6 = NameChecker.Compare("media.example.com", new[] { IPAddress.Parse("198.51.100.1"), IPAddress.Parse("2001:db8::7") }, "203.0.113.7", "2001:db8::7");
        Assert.True(v6.Resolves);
        Assert.Equal("2001:db8::7", v6.PublicAddress);
        Assert.Equal(new[] { "198.51.100.1", "2001:db8::7" }, v6.Addresses);
    }

    [Fact]
    public void Name_PointingElsewhereIsExplained()
    {
        var status = NameChecker.Compare("media.example.com", new[] { IPAddress.Parse("198.51.100.1") }, "203.0.113.7", string.Empty);
        Assert.False(status.Resolves);
        Assert.Equal("media.example.com points to 198.51.100.1, not to this server at 203.0.113.7. Check its DNS record.", status.Error);
        AssertPlain(status.Error);
    }

    [Fact]
    public void Name_NoAnswerOrUnknownPublicAddressIsExplained()
    {
        var none = NameChecker.Compare("media.example.com", Array.Empty<IPAddress>(), "203.0.113.7", string.Empty);
        Assert.False(none.Resolves);
        Assert.StartsWith("media.example.com does not resolve", none.Error, StringComparison.Ordinal);

        var unknown = NameChecker.Compare("media.example.com", new[] { IPAddress.Parse("198.51.100.1") }, string.Empty, string.Empty);
        Assert.False(unknown.Resolves);
        Assert.Equal(string.Empty, unknown.PublicAddress);
        Assert.StartsWith("This server's public address is not known yet", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NameChecker_KeepsTheAnswerForAMinute()
    {
        var lookups = 0;
        using var names = new NameChecker(
            (_, _) =>
            {
                lookups++;
                return Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") });
            },
            _ => Task.FromResult(("203.0.113.7", string.Empty)));

        Assert.Equal("media.example.com", names.Cached("media.example.com").Hostname);
        Assert.False(names.Cached("media.example.com").Resolves);
        Assert.True((await names.GetAsync("media.example.com", CancellationToken.None)).Resolves);
        Assert.True((await names.GetAsync("media.example.com", CancellationToken.None)).Resolves);
        Assert.True(names.Cached("media.example.com").Resolves);
        Assert.Equal(1, lookups);

        // Another name is looked up at once.
        await names.GetAsync("other.example.com", CancellationToken.None);
        Assert.Equal(2, lookups);
        Assert.Equal(string.Empty, (await names.GetAsync(string.Empty, CancellationToken.None)).Hostname);
    }

    [Fact]
    public async Task NameChecker_UnknownNameIsAnError()
    {
        using var names = new NameChecker(
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            _ => Task.FromResult(("203.0.113.7", string.Empty)));

        var status = await names.GetAsync("media.example.com", CancellationToken.None);

        Assert.False(status.Resolves);
        Assert.StartsWith("media.example.com does not resolve", status.Error, StringComparison.Ordinal);
    }

    // ---- The outside check ----

    private const string StartJson = """
        {"ok":1,"request_id":"1a2b3c4d","permanent_link":"https://check-host.net/check-report/1a2b3c4d",
         "nodes":{"de1.node.check-host.net":["de","Germany","Frankfurt","5.9.1.2","AS24940"],
                  "us2.node.check-host.net":["us","USA","Chicago","5.9.1.3","AS12345"],
                  "jp1.node.check-host.net":["jp","Japan","","5.9.1.4","AS6789"]}}
        """;

    private const string DoneJson = """
        {"de1.node.check-host.net":[{"time":0.031,"address":"203.0.113.7"}],
         "us2.node.check-host.net":[{"error":"Connection timed out"}],
         "jp1.node.check-host.net":[{"error":"Connection refused"}]}
        """;

    [Fact]
    public void Reachability_ReadsTheStart()
    {
        var start = ReachabilityChecker.ParseStart(StartJson);
        Assert.NotNull(start);
        Assert.Equal("1a2b3c4d", start!.Value.RequestId);
        Assert.Equal(
            new[] { ("de1.node.check-host.net", "Frankfurt, Germany"), ("us2.node.check-host.net", "Chicago, USA"), ("jp1.node.check-host.net", "Japan") },
            start.Value.Nodes);
    }

    [Theory]
    [InlineData("{\"error\":\"limit_exceeded\"}")]
    [InlineData("{\"ok\":0,\"request_id\":\"x\"}")]
    [InlineData("{\"ok\":1}")]
    [InlineData("<html>")]
    [InlineData("[]")]
    public void Reachability_StartThatDidNotStartIsNull(string json)
        => Assert.Null(ReachabilityChecker.ParseStart(json));

    [Fact]
    public void Reachability_ReadsPendingSuccessAndFailure()
    {
        var states = ReachabilityChecker.ParseResult("""
            {"a":null,"b":[{"time":0.12,"address":"1.2.3.4"}],"c":[{"error":"Connection timed out"}],"d":[null],"e":[[{"time":0.2}]]}
            """);
        Assert.NotNull(states);
        Assert.Null(states!["a"]);
        Assert.Equal((true, string.Empty), states["b"]);
        Assert.Equal((false, "The connection timed out."), states["c"]);
        Assert.Null(states["d"]);
        Assert.Equal((true, string.Empty), states["e"]);
        Assert.Null(ReachabilityChecker.ParseResult("nope"));
    }

    [Fact]
    public void Reachability_OneLocationConnectingIsEnough()
    {
        var start = ReachabilityChecker.ParseStart(StartJson)!.Value;
        var when = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var result = ReachabilityChecker.Aggregate(start.Nodes, ReachabilityChecker.ParseResult(DoneJson)!, when);

        Assert.True(result.Reachable);
        Assert.Equal(when, result.CheckedUtc);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(3, result.Nodes.Count);
        Assert.True(result.Nodes[0].Ok);
        Assert.Equal("Frankfurt, Germany", result.Nodes[0].Location);
        Assert.Equal("The connection timed out.", result.Nodes[1].Error);
        Assert.Equal("The connection was refused.", result.Nodes[2].Error);
    }

    [Fact]
    public void Reachability_NoLocationConnectingIsExplained()
    {
        var start = ReachabilityChecker.ParseStart(StartJson)!.Value;
        var states = ReachabilityChecker.ParseResult("""{"de1.node.check-host.net":[{"error":"Connection timed out"}],"us2.node.check-host.net":null}""")!;
        var result = ReachabilityChecker.Aggregate(start.Nodes, states, DateTime.UtcNow);

        Assert.False(result.Reachable);
        Assert.Equal("No outside location could connect to port 443. Check that the router forwards port 443 to this server.", result.Error);
        Assert.Equal("The check did not finish in time.", result.Nodes[1].Error);
        Assert.Equal("The check did not finish in time.", result.Nodes[2].Error);
        AssertPlain(result.Error);
    }

    [Fact]
    public async Task Reachability_RunsAndKeepsTheResult()
    {
        var asked = new List<Uri>();
        var polls = 0;
        var http = StubHttp.Factory(request =>
        {
            asked.Add(request.RequestUri!);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            if (request.RequestUri!.AbsolutePath == "/check-tcp")
            {
                return (HttpStatusCode.OK, StartJson);
            }

            // The first poll is still pending everywhere.
            return (HttpStatusCode.OK, ++polls == 1
                ? """{"de1.node.check-host.net":null,"us2.node.check-host.net":null,"jp1.node.check-host.net":null}"""
                : DoneJson);
        });
        var checker = new ReachabilityChecker(http, (_, _) => Task.CompletedTask);
        Assert.Null(checker.Last.Reachable);
        Assert.Null(checker.Last.CheckedUtc);

        var result = await checker.CheckAsync("media.example.com", CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal(3, result.Nodes.Count);
        Assert.True(checker.Last.Reachable);

        // Only the name and the port leave this server.
        Assert.Equal("https://check-host.net/check-tcp?host=media.example.com:443&max_nodes=3", asked[0].OriginalString);
        Assert.All(asked.Skip(1), u => Assert.Equal("https://check-host.net/check-result/1a2b3c4d", u.OriginalString));
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task Reachability_ServiceFailuresAreExplained()
    {
        var down = new ReachabilityChecker(StubHttp.Always(HttpStatusCode.BadGateway, "bad gateway"), (_, _) => Task.CompletedTask);
        var result = await down.CheckAsync("media.example.com", CancellationToken.None);
        Assert.Null(result.Reachable);
        Assert.Equal("The outside check could not be reached.", result.Error);
        Assert.NotNull(result.CheckedUtc);

        var busy = new ReachabilityChecker(StubHttp.Always(HttpStatusCode.OK, "{\"error\":\"limit_exceeded\"}"), (_, _) => Task.CompletedTask);
        Assert.Equal("The outside check is busy right now. Try again in a minute.", (await busy.CheckAsync("media.example.com", CancellationToken.None)).Error);

        var throws = new ReachabilityChecker(StubHttp.Factory(_ => throw new HttpRequestException("no route")), (_, _) => Task.CompletedTask);
        Assert.Equal("The outside check could not be reached.", (await throws.CheckAsync("media.example.com", CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*.example.com")]
    [InlineData("nas.lan")]
    [InlineData("203.0.113.7")]
    public async Task Reachability_NeedsAPublicName(string hostname)
    {
        var asked = 0;
        var checker = new ReachabilityChecker(StubHttp.Factory(_ => { asked++; return (HttpStatusCode.OK, StartJson); }), (_, _) => Task.CompletedTask);

        var result = await checker.CheckAsync(hostname, CancellationToken.None);

        Assert.Null(result.Reachable);
        Assert.Equal("Add a public address people use from the internet first.", result.Error);
        Assert.Equal(0, asked);
    }

    // ---- On-screen text ----

    [Fact]
    public void ScreenText_IsPlain()
    {
        var texts = new List<string>
        {
            PortMapResult.Taken(443).Error,
            RouterPortMapper.TakenSentence(new[] { 80, 443 }),
            RouterPortMapper.NoRouterSentence(new[] { new PortMapping(80, 80, _lan, 1), new PortMapping(443, 443, _lan, 1) }),
            CgnatCheck.Evaluate("100.71.2.3", string.Empty).Reason,
            CgnatCheck.Evaluate("192.168.0.5", string.Empty).Reason,
            CgnatCheck.Evaluate("203.0.113.7", "198.51.100.4").Reason,
            UpnpProtocol.Describe(606, 443),
            UpnpProtocol.Describe(402, 443),
        };
        texts.AddRange(Enumerable.Range(0, 6).Select(c => NatPmpPacket.Describe(c, 443)));
        Assert.All(texts, AssertPlain);
    }

    // No semicolons or parentheses, and at most two sentences.
    private static void AssertPlain(string text)
    {
        Assert.DoesNotContain(";", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(")", text, StringComparison.Ordinal);
        var sentences = text.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(sentences <= 2, text);
    }
}
