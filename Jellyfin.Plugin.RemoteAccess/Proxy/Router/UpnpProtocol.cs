using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Builds and reads the UPnP Internet Gateway Device messages: SSDP search replies, the device description and
/// SOAP control calls. Kept apart from the network so every reader can be tested with captured router replies.
/// </summary>
internal static class UpnpProtocol
{
    /// <summary>The SSDP multicast address and port routers listen on.</summary>
    public static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>UPnP error 718: the port is already forwarded to another device.</summary>
    public const int ConflictInMappingEntry = 718;

    /// <summary>UPnP error 725: the router only keeps forwards with no lifetime.</summary>
    public const int OnlyPermanentLeasesSupported = 725;

    /// <summary>UPnP error 606: the router does not let this device change forwards.</summary>
    public const int ActionNotAuthorized = 606;

    /// <summary>UPnP error 714: no forward exists for that port.</summary>
    public const int NoSuchEntryInArray = 714;

    /// <summary>
    /// Gets the services that forward ports, best first. A router offers one of them on its WAN connection.
    /// </summary>
    public static IReadOnlyList<string> ServiceTypes { get; } = new[]
    {
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    };

    // Settings that never fetch a DTD or entity: a description comes from a device on the network, not a
    // trusted source.
    private static readonly XmlReaderSettings _xml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1_000_000,
        IgnoreComments = true
    };

    /// <summary>
    /// Builds an SSDP search for one service type.
    /// </summary>
    /// <param name="searchTarget">The service type.</param>
    /// <returns>The message bytes.</returns>
    public static byte[] SearchRequest(string searchTarget)
        => Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n"
            + "HOST: 239.255.255.250:1900\r\n"
            + "MAN: \"ssdp:discover\"\r\n"
            + "MX: 2\r\n"
            + "ST: " + searchTarget + "\r\n"
            + "\r\n");

    /// <summary>
    /// Reads an SSDP search reply.
    /// </summary>
    /// <param name="reply">The reply text.</param>
    /// <returns>The description address and the search target it answered, or null when the reply is not a
    /// successful answer for a port forwarding service or gateway.</returns>
    public static (Uri Location, string SearchTarget)? ParseSearchReply(string reply)
    {
        if (string.IsNullOrEmpty(reply))
        {
            return null;
        }

        var lines = reply.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || !lines[0].Contains(" 200", StringComparison.Ordinal))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (!headers.TryGetValue("LOCATION", out var location)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        var target = headers.GetValueOrDefault("ST") ?? string.Empty;
        var wanted = ServiceTypes.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase))
            || target.StartsWith("urn:schemas-upnp-org:device:InternetGatewayDevice:", StringComparison.OrdinalIgnoreCase);
        return wanted ? (uri, target) : null;
    }

    /// <summary>
    /// Finds the port forwarding service in a device description and the address its control calls go to.
    /// </summary>
    /// <param name="xml">The description.</param>
    /// <param name="location">Where the description came from, which relative addresses are read against.</param>
    /// <returns>The service type and control address, or null when the device has no such service, or its
    /// control address points at another machine.</returns>
    public static (string ServiceType, Uri ControlUrl)? FindControl(string xml, Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (Load(xml) is not { } doc)
        {
            return null;
        }

        // Older devices give a base address for relative ones. Newer ones leave it out, and the description's own
        // address is the base.
        var baseText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        var root = !string.IsNullOrEmpty(baseText) && Uri.TryCreate(baseText, UriKind.Absolute, out var given) ? given : location;
        var services = doc.Descendants().Where(e => e.Name.LocalName == "service").ToList();
        foreach (var type in ServiceTypes)
        {
            var service = services.FirstOrDefault(s => string.Equals(Child(s, "serviceType"), type, StringComparison.OrdinalIgnoreCase));
            var control = service is null ? null : Child(service, "controlURL");
            if (string.IsNullOrEmpty(control) || !Uri.TryCreate(root, control, out var url))
            {
                continue;
            }

            // Control calls carry this machine's address. They only ever go to the device that answered the
            // search, never to another host its description names.
            if (url.Scheme != Uri.UriSchemeHttp || !string.Equals(url.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return (type, url);
        }

        return null;
    }

    /// <summary>
    /// Builds a SOAP control call.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="action">The action, such as AddPortMapping.</param>
    /// <param name="arguments">The arguments, in the order the service defines.</param>
    /// <returns>The envelope.</returns>
    public static string Envelope(string serviceType, string action, IEnumerable<(string Name, string Value)> arguments)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?>")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
            .Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");
        foreach (var (name, value) in arguments)
        {
            body.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        }

        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return body.ToString();
    }

    /// <summary>
    /// Builds the arguments of AddPortMapping.
    /// </summary>
    /// <param name="mapping">The forward.</param>
    /// <param name="lifetimeSeconds">The lease to ask for, 0 for one with no end.</param>
    /// <returns>The arguments.</returns>
    public static IEnumerable<(string Name, string Value)> AddArguments(PortMapping mapping, int lifetimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new[]
        {
            ("NewRemoteHost", string.Empty),
            ("NewExternalPort", mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewProtocol", "TCP"),
            ("NewInternalPort", mapping.InternalPort.ToString(CultureInfo.InvariantCulture)),
            ("NewInternalClient", mapping.LanAddress.ToString()),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", PortMapping.Description),
            ("NewLeaseDuration", lifetimeSeconds.ToString(CultureInfo.InvariantCulture))
        };
    }

    /// <summary>
    /// Builds the arguments that name one forward, for DeletePortMapping and GetSpecificPortMappingEntry.
    /// </summary>
    /// <param name="externalPort">The external port.</param>
    /// <returns>The arguments.</returns>
    public static IEnumerable<(string Name, string Value)> PortArguments(int externalPort) => new[]
    {
        ("NewRemoteHost", string.Empty),
        ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
        ("NewProtocol", "TCP")
    };

    /// <summary>
    /// Reads one value from a SOAP response, such as NewExternalIPAddress.
    /// </summary>
    /// <param name="xml">The response.</param>
    /// <param name="name">The value's element name.</param>
    /// <returns>The value, or null when the response has none.</returns>
    public static string? ReadValue(string xml, string name)
        => Load(xml)?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    /// <summary>
    /// Reads the UPnP error from a SOAP fault.
    /// </summary>
    /// <param name="xml">The response.</param>
    /// <returns>The error code and description, or null when the response is not a UPnP fault.</returns>
    public static (int Code, string Description)? ReadFault(string xml)
    {
        var doc = Load(xml);
        var code = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value.Trim();
        if (doc is null || !int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return (value, doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Says what a UPnP error means for the administrator.
    /// </summary>
    /// <param name="code">The error code.</param>
    /// <param name="port">The external port asked for.</param>
    /// <returns>A plain sentence.</returns>
    public static string Describe(int code, int port) => code switch
    {
        ActionNotAuthorized => "The router does not allow this server to open ports. Turn on UPnP for it in the router's settings.",
        ConflictInMappingEntry => $"The router already forwards port {port} to another device.",
        _ => $"The router could not open port {port}."
    };

    private static string? Child(XElement element, string name)
        => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static XDocument? Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, _xml);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
