using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Normalizes proxy settings and reports what is wrong with them. Normalizing first means a save never
/// stores half-valid values: hostnames are lower-cased, numbers are clamped and lists are cleaned.
/// </summary>
public static class ProxySettingsValidator
{
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
        a.TrustedNetworks = CleanEntries(a.TrustedNetworks);
        a.BlockedAddresses = CleanEntries(a.BlockedAddresses);
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
    }

    /// <summary>
    /// Lists every problem with already-normalized settings. Empty means valid.
    /// </summary>
    /// <param name="settings">The normalized settings.</param>
    /// <param name="rawHostnames">The hostnames as entered, to report ones normalization dropped.</param>
    /// <returns>Human-readable problems.</returns>
    public static List<string> Validate(ProxySettings settings, IEnumerable<string>? rawHostnames = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        // Enum values can arrive as any number. One that names nothing would fail to save as XML, which would
        // leave Jellyfin unable to store the plugin's settings at all, so it is refused here.
        CheckEnum(errors, "Certificate source", settings.Certificate.Mode);
        CheckEnum(errors, "Country rule", settings.Advanced.CountryMode ?? CountryRuleMode.Off);

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
        return errors;
    }

    private static List<string> CleanEntries(List<string>? values)
        => (values ?? new List<string>())
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
}
