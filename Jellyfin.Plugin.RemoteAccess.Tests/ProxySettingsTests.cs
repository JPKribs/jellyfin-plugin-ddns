using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Normalization and validation of proxy settings, and how unset advanced values resolve to defaults.
/// </summary>
public class ProxySettingsTests
{
    private static List<string> Errors(ProxySettings s, IEnumerable<string>? raw = null)
    {
        ProxySettingsValidator.Normalize(s);
        return ProxySettingsValidator.Validate(s, raw);
    }

    [Fact]
    public void Defaults_AreValid()
        => Assert.Empty(Errors(new ProxySettings()));

    [Fact]
    public void UnsetValues_FollowTheDefaults()
    {
        var o = ProxyOptions.From(new ProxyAdvancedSettings());
        // Lists compare by reference in a record, so they are checked separately.
        Assert.Equal(ProxyOptions.Defaults, o with { TrustedNetworks = ProxyOptions.Defaults.TrustedNetworks, BlockedAddresses = ProxyOptions.Defaults.BlockedAddresses, Countries = ProxyOptions.Defaults.Countries });
        Assert.Empty(o.TrustedNetworks);
        Assert.Empty(o.Countries);
        Assert.Equal(80, o.HttpPort);
        Assert.Equal(443, o.HttpsPort);
    }

    [Fact]
    public void LowMemory_HoldsFewerConnections_AndKeepsTheRest()
    {
        var low = ProxyOptions.From(new ProxyAdvancedSettings(), ProxyOptions.LowMemory);
        Assert.True(low.MaxConnections < ProxyOptions.Standard.MaxConnections);
        Assert.True(low.MaxConnectionsPerIp < ProxyOptions.Standard.MaxConnectionsPerIp);
        Assert.True(low.KeepAliveSeconds < ProxyOptions.Standard.KeepAliveSeconds);
        Assert.Equal(ProxyOptions.Standard.MaxRequestBodyMbWan, low.MaxRequestBodyMbWan);

        // A value the administrator chose wins over either set.
        Assert.Equal(1000, ProxyOptions.From(new ProxyAdvancedSettings { MaxConnections = 1000 }, ProxyOptions.LowMemory).MaxConnections);
        Assert.Empty(Errors(new ProxySettings()));
    }

    [Fact]
    public void ChangedValues_Win()
    {
        var o = ProxyOptions.From(new ProxyAdvancedSettings { HttpsPort = 9443, KeepAliveSeconds = 60, HstsEnabled = false });
        Assert.Equal(9443, o.HttpsPort);
        Assert.Equal(60, o.KeepAliveSeconds);
        Assert.False(o.HstsEnabled);
        Assert.Equal(ProxyOptions.Defaults.RequestHeadersTimeoutSeconds, o.RequestHeadersTimeoutSeconds);
    }

    [Fact]
    public void Normalize_KeepsUnsetValuesUnset()
    {
        var s = new ProxySettings();
        ProxySettingsValidator.Normalize(s);
        Assert.Null(s.Advanced.HttpsPort);
        Assert.Null(s.Advanced.KeepAliveSeconds);
        Assert.Null(s.Advanced.HstsEnabled);
    }

    [Fact]
    public void Normalize_CleansHostnamesAndClamps()
    {
        var s = new ProxySettings { Hostnames = new List<string> { " Media.Example.com ", "media.example.com", "bad host" } };
        s.Advanced.BackendActivityTimeoutSeconds = 10;
        s.Advanced.MaxConnections = -5;
        ProxySettingsValidator.Normalize(s);

        Assert.Equal(new[] { "media.example.com" }, s.Hostnames);
        Assert.Equal(61, s.Advanced.BackendActivityTimeoutSeconds);
        Assert.Equal(0, s.Advanced.MaxConnections);
    }

    [Fact]
    public void Normalize_FillsMissingSections()
    {
        var s = new ProxySettings { Advanced = null!, Certificate = null! };
        ProxySettingsValidator.Normalize(s);
        Assert.NotNull(s.Advanced);
        Assert.NotNull(s.Certificate);
    }

    [Fact]
    public void Validate_ReportsDroppedHostnames()
    {
        var raw = new List<string> { "media.example.com", "not a host" };
        var s = new ProxySettings { Hostnames = raw.ToList() };
        Assert.Contains(Errors(s, raw), e => e.Contains("not a host", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PortClashes()
    {
        Assert.NotEmpty(Errors(new ProxySettings { Advanced = new ProxyAdvancedSettings { HttpPort = 8920, HttpsPort = 8920 } }));
        Assert.NotEmpty(Errors(new ProxySettings { Advanced = new ProxyAdvancedSettings { HttpsPort = 70000 } }));
    }

    [Fact]
    public void Validate_CertificateNeedsFiles()
    {
        var pfx = new ProxySettings();
        pfx.Certificate.Mode = CertificateMode.Pkcs12File;
        Assert.NotEmpty(Errors(pfx));
        var pem = new ProxySettings();
        pem.Certificate.Mode = CertificateMode.PemFiles;
        pem.Certificate.Path = "/etc/cert.pem";
        Assert.NotEmpty(Errors(pem));
    }

    [Theory]
    [InlineData("http://192.168.1.20:5055", true)]
    [InlineData("https://nas.local:8443", true)]
    [InlineData("ftp://192.168.1.20", false)]
    [InlineData("192.168.1.20:5055", false)]
    [InlineData("http://169.254.169.254/latest", false)]
    [InlineData("http://[fe80::1]:80", false)]
    [InlineData("http://100.100.100.200/", false)]
    [InlineData("http://metadata.google.internal/computeMetadata", false)]
    [InlineData("http://Metadata.Azure.com.", false)]
    public void Validate_RouteTargets(string target, bool valid)
    {
        var s = new ProxySettings();
        s.Routes.Add(new ProxyRoute { Name = "R", Hostnames = new List<string> { "r.example.com" }, Target = target });
        Assert.Equal(valid, Errors(s).Count == 0);
    }

    [Fact]
    public void Validate_HostnameUsedTwice()
    {
        var s = new ProxySettings { Hostnames = new List<string> { "media.example.com" } };
        s.Routes.Add(new ProxyRoute { Name = "R", Hostnames = new List<string> { "media.example.com" }, Target = "http://10.0.0.2" });
        Assert.Contains(Errors(s), e => e.Contains("used by both", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PinnedNeedsThumbprint()
    {
        var s = new ProxySettings();
        s.Routes.Add(new ProxyRoute { Name = "R", Hostnames = new List<string> { "r.example.com" }, Target = "https://10.0.0.2", BackendTls = BackendTlsMode.Pinned, PinnedThumbprint = "ab:cd" });
        Assert.NotEmpty(Errors(s));
        s.Routes[0].PinnedThumbprint = new string('a', 64);
        Assert.Empty(Errors(s));
        Assert.Equal(new string('A', 64), s.Routes[0].PinnedThumbprint);
    }

    [Fact]
    public void Validate_TrustedNetworks()
    {
        var s = new ProxySettings();
        s.Advanced.TrustedNetworks = new List<string> { "100.64.0.0/10", "10.1.2.3", "nope" };
        Assert.Single(Errors(s));
    }
}
