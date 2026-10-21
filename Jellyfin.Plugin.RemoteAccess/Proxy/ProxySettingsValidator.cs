using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Normalizes proxy settings and reports what is wrong with them. Normalizing first means a save never
/// stores half-valid values: hostnames are lower-cased, numbers are clamped and lists are cleaned.
/// </summary>
public static class ProxySettingsValidator
{
    // Cloud metadata services. A route pointing here would let anyone who reaches the route read the host's
    // cloud credentials, so these are refused even though only administrators configure routes.
    private static readonly IPNetwork[] _metadataNetworks =
    {
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fd00:ec2::/32"),
        IPNetwork.Parse("fd20:ce::254/128"),
        IPNetwork.Parse("100.100.100.200/32"),
        IPNetwork.Parse("192.0.0.192/32"),
        IPNetwork.Parse("168.63.129.16/32")
    };

    // Cloud metadata services reached by name, which would hand a service route the machine's credentials.
    private static readonly HashSet<string> _metadataHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.google.internal", "metadata", "instance-data", "instance-data.ec2.internal", "metadata.azure.com"
    };

    /// <summary>
    /// Cleans the settings in place: normalizes hostnames and networks and clamps numbers to sane ranges.
    /// Empty advanced values stay empty, so they keep following the defaults.
    /// </summary>
    /// <param name="settings">The settings to clean.</param>
    public static void Normalize(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Hostnames = HostnameMatcher.NormalizeAll(settings.Hostnames);
        settings.Certificate ??= new ProxyCertificateSettings();
        settings.Certificate.Path = (settings.Certificate.Path ?? string.Empty).Trim();
        settings.Certificate.KeyPath = (settings.Certificate.KeyPath ?? string.Empty).Trim();
        settings.Certificate.Password ??= string.Empty;
        var a = settings.Advanced ??= new ProxyAdvancedSettings();
        a.LanAddress = (a.LanAddress ?? string.Empty).Trim();
        a.TrustedNetworks = (a.TrustedNetworks ?? new List<string>())
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        a.BlockedAddresses = (a.BlockedAddresses ?? new List<string>())
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        a.Countries = (a.Countries ?? new List<string>())
            .Select(c => (c ?? string.Empty).Trim().ToUpperInvariant())
            .Where(c => c.Length == 2 && c.All(char.IsAsciiLetterUpper))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        a.HstsMaxAgeDays = ClampOptional(a.HstsMaxAgeDays, 1, 730);
        a.KeepAliveSeconds = ClampOptional(a.KeepAliveSeconds, 5, 3600);
        a.RequestHeadersTimeoutSeconds = ClampOptional(a.RequestHeadersTimeoutSeconds, 1, 300);
        a.MaxRequestHeadersKb = ClampOptional(a.MaxRequestHeadersKb, 8, 1024);
        a.MaxConnections = ClampOptional(a.MaxConnections, 0, int.MaxValue);
        a.MaxConnectionsPerIp = ClampOptional(a.MaxConnectionsPerIp, 0, int.MaxValue);
        a.BanFailures = ClampOptional(a.BanFailures, 0, 1000);
        a.BanWindowMinutes = ClampOptional(a.BanWindowMinutes, 1, 1440);
        a.BanMinutes = ClampOptional(a.BanMinutes, 0, 525600);
        a.MaxRequestBodyMbWan = ClampOptional(a.MaxRequestBodyMbWan, 0, int.MaxValue);
        a.MaxRequestBodyMbLan = ClampOptional(a.MaxRequestBodyMbLan, 0, int.MaxValue);
        a.MinResponseBytesPerSecond = ClampOptional(a.MinResponseBytesPerSecond, 0, int.MaxValue);
        a.BackendActivityTimeoutSeconds = ClampOptional(a.BackendActivityTimeoutSeconds, 61, 86400);

        settings.Routes ??= new List<ProxyRoute>();
        settings.Routes.RemoveAll(r => r is null);

        // Route ids name YARP routes, which compare them ignoring case: a repeat, or one of Jellyfin's own,
        // would make YARP refuse the whole configuration, so such an id is replaced.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProxyRuntime.JellyfinId, ProxyRuntime.JellyfinMediaId };
        foreach (var route in settings.Routes)
        {
            var id = (route.Id ?? string.Empty).Trim();
            route.Id = id.Length == 0 || !ids.Add(id) ? Guid.NewGuid().ToString("N") : id;
            route.Name = (route.Name ?? string.Empty).Trim();
            route.Hostnames = HostnameMatcher.NormalizeAll(route.Hostnames);
            route.Target = (route.Target ?? string.Empty).Trim();
            route.PinnedThumbprint = new string((route.PinnedThumbprint ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

            var o = route.Overrides ??= new RouteOverrides();
            o.MaxRequestBodyMbWan = ClampOptional(o.MaxRequestBodyMbWan, 0, int.MaxValue);
            o.MaxRequestBodyMbLan = ClampOptional(o.MaxRequestBodyMbLan, 0, int.MaxValue);
            o.BackendActivityTimeoutSeconds = ClampOptional(o.BackendActivityTimeoutSeconds, 61, 86400);
            o.MinResponseBytesPerSecond = ClampOptional(o.MinResponseBytesPerSecond, 0, int.MaxValue);
            o.HstsMaxAgeDays = ClampOptional(o.HstsMaxAgeDays, 1, 730);
        }
    }

    /// <summary>
    /// Lists every problem with already-normalized settings. Empty means valid.
    /// </summary>
    /// <param name="settings">The normalized settings.</param>
    /// <param name="rawHostnames">The hostnames as entered, to report ones normalization dropped.</param>
    /// <param name="reservedPorts">Jellyfin's and the proxy's ports, which a Service on this machine must not use.</param>
    /// <returns>Human-readable problems.</returns>
    public static List<string> Validate(ProxySettings settings, IEnumerable<string>? rawHostnames = null, IReadOnlyCollection<int>? reservedPorts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        // Enum values can arrive as any number. One that names nothing would fail to save as XML, which would
        // leave Jellyfin unable to store the plugin's settings at all, so it is refused here.
        CheckEnum(errors, "Certificate source", settings.Certificate.Mode);
        CheckEnum(errors, "Country rule", settings.Advanced.CountryMode ?? CountryRuleMode.Off);
        foreach (var route in settings.Routes)
        {
            CheckEnum(errors, "Service exposure", route.Exposure);
            CheckEnum(errors, "Service certificate check", route.BackendTls);
        }

        var o = ProxyOptions.From(settings.Advanced);
        CheckPort(errors, "HTTP port", o.HttpPort);
        CheckPort(errors, "HTTPS port", o.HttpsPort);
        if (o.HttpPort == o.HttpsPort)
        {
            errors.Add("The HTTP and HTTPS ports must be different.");
        }

        // Local clients are then sent to the public address, so there has to be one that is not a wildcard.
        if (o.JellyfinLoopbackOnly && !settings.Hostnames.Any(h => !h.StartsWith('*')))
        {
            errors.Add("Only through the proxy needs an address that is not a wildcard, so local apps can be sent to it.");
        }

        if (o.LanAddress.Length > 0 && !IPAddress.TryParse(o.LanAddress, out _))
        {
            errors.Add($"LAN address \"{o.LanAddress}\" is not an IP address.");
        }

        if (rawHostnames is not null)
        {
            foreach (var raw in rawHostnames.Where(r => !string.IsNullOrWhiteSpace(r) && HostnameMatcher.Normalize(r).Length == 0))
            {
                errors.Add($"\"{raw.Trim()}\" is not a valid hostname.");
            }
        }

        if (o.CountryMode == CountryRuleMode.AllowOnly && o.Countries.Count == 0)
        {
            errors.Add("Allowing only listed countries needs at least one country, or every internet connection would be refused.");
        }

        foreach (var raw in o.TrustedNetworks.Where(n => !NetworkClassifier.TryParseNetwork(n, out _)))
        {
            errors.Add($"Trusted network \"{raw}\" is not an address or CIDR block.");
        }

        foreach (var raw in o.BlockedAddresses.Where(n => !NetworkClassifier.TryParseNetwork(n, out _)))
        {
            errors.Add($"Blocked address \"{raw}\" is not an address or CIDR block.");
        }

        CheckCertificate(errors, settings.Certificate);
        CheckRoutes(errors, settings, reservedPorts);
        return errors;
    }

    private static int? ClampOptional(int? value, int min, int max)
        => value is { } v ? Math.Clamp(v, min, max) : null;

    private static void CheckEnum<T>(List<string> errors, string label, T value)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add($"{label} has an unknown value.");
        }
    }

    private static void CheckPort(List<string> errors, string label, int port)
    {
        if (port is < 1 or > 65535)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{label} must be between 1 and 65535."));
        }
    }

    private static void CheckCertificate(List<string> errors, ProxyCertificateSettings certificate)
    {
        switch (certificate.Mode)
        {
            case CertificateMode.Pkcs12File when certificate.Path.Length == 0:
                errors.Add("Choose the .pfx or .p12 certificate file.");
                break;
            case CertificateMode.PemFiles when certificate.Path.Length == 0 || certificate.KeyPath.Length == 0:
                errors.Add("Choose both the certificate and the private key file.");
                break;
        }
    }

    private static void CheckRoutes(List<string> errors, ProxySettings settings, IReadOnlyCollection<int>? reservedPorts)
    {
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in settings.Hostnames)
        {
            claimed[name] = "Jellyfin";
        }

        foreach (var route in settings.Routes)
        {
            var label = route.Name.Length > 0 ? route.Name : "A route";
            if (route.Hostnames.Count == 0)
            {
                errors.Add($"{label} needs at least one hostname.");
            }

            foreach (var name in route.Hostnames)
            {
                if (claimed.TryGetValue(name, out var owner))
                {
                    errors.Add($"{name} is used by both {owner} and {label}.");
                }
                else
                {
                    claimed[name] = label;
                }
            }

            if (!Uri.TryCreate(route.Target, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
                || target.Host.Length == 0)
            {
                errors.Add($"{label} needs a target address, for example http://192.168.1.20:5055.");
                continue;
            }

            // A trailing dot still resolves, to the address itself for one written as digits.
            var host = target.Host.Trim('[', ']').TrimEnd('.');
            if ((IPAddress.TryParse(host, out var ip) && IsMetadataAddress(ip)) || _metadataHosts.Contains(host))
            {
                errors.Add($"{label} points at a cloud metadata address, which is not allowed.");
            }

            if (reservedPorts is not null && SelfTarget.IsReserved(target, reservedPorts))
            {
                errors.Add($"{label} points at Jellyfin or this proxy on this server. Jellyfin is already served on your address, and a Service is for other apps.");
            }

            if (route.BackendTls == BackendTlsMode.Pinned && route.PinnedThumbprint.Length != 64)
            {
                errors.Add($"{label} needs the service certificate's SHA-256 thumbprint, 64 hexadecimal characters.");
            }
        }
    }

    /// <summary>
    /// Whether an address is a cloud metadata service or link-local, which a Service must never reach.
    /// </summary>
    /// <param name="ip">The address.</param>
    /// <returns>True when it must not be connected to.</returns>
    internal static bool IsMetadataAddress(IPAddress ip)
    {
        var normalized = NetworkClassifier.Normalize(ip);
        if (Models.IPAddressClassifier.EmbeddedIPv4(normalized) is { } inside && IsMetadataAddress(inside))
        {
            return true;
        }

        return _metadataNetworks.Any(n => n.BaseAddress.AddressFamily == normalized.AddressFamily && n.Contains(normalized))
            || (normalized.AddressFamily == AddressFamily.InterNetworkV6 && normalized.IsIPv6LinkLocal);
    }
}
