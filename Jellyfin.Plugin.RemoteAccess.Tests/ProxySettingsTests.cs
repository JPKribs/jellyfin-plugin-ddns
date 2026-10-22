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

    [Fact]
    public void SavedSettings_WithServicesFromAnOlderVersion_StillLoad()
    {
        // Services were removed. A settings file written before that still carries them, and must load as before.
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Proxy>
                <Enabled>true</Enabled>
                <Hostnames>
                  <string>media.example.com</string>
                </Hostnames>
                <Routes>
                  <ProxyRoute>
                    <Id>req</Id>
                    <Name>Requests</Name>
                    <Enabled>true</Enabled>
                    <Hostnames>
                      <string>requests.example.com</string>
                    </Hostnames>
                    <Target>http://192.168.1.20:5055</Target>
                    <Exposure>LanOnly</Exposure>
                    <BackendTls>Pinned</BackendTls>
                    <Overrides>
                      <MaxRequestBodyMbWan>1</MaxRequestBodyMbWan>
                      <Compress>true</Compress>
                    </Overrides>
                  </ProxyRoute>
                </Routes>
                <Advanced>
                  <HttpsPort>8443</HttpsPort>
                </Advanced>
              </Proxy>
            </PluginConfiguration>
            """;
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Jellyfin.Plugin.DynamicDns.Configuration.PluginConfiguration));
        using var reader = new System.IO.StringReader(Xml);
        var config = (Jellyfin.Plugin.DynamicDns.Configuration.PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.True(config.Proxy.Enabled);
        Assert.Equal(new[] { "media.example.com" }, config.Proxy.Hostnames);
        Assert.Equal(8443, config.Proxy.Advanced.HttpsPort);

        // The dashboard's save body is JSON, and an older page may still send them there too.
        var json = """{"Enabled":true,"Hostnames":["media.example.com"],"Routes":[{"Id":"req","Hostnames":["requests.example.com"],"Target":"http://192.168.1.20:5055"}]}""";
        var saved = System.Text.Json.JsonSerializer.Deserialize<ProxySettings>(json)!;
        Assert.Equal(new[] { "media.example.com" }, saved.Hostnames);
        Assert.Empty(Errors(saved));
    }

    [Fact]
    public void Validate_TrustedNetworks()
    {
        var s = new ProxySettings();
        s.Advanced.TrustedNetworks = new List<string> { "100.64.0.0/10", "10.1.2.3", "nope" };
        Assert.Single(Errors(s));
    }
}
