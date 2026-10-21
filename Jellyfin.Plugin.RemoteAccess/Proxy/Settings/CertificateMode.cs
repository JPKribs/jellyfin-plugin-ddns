namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Where the proxy's TLS certificate comes from.
/// </summary>
public enum CertificateMode
{
    /// <summary>No certificate. The HTTPS listener does not start.</summary>
    None,

    /// <summary>Issued and renewed from Let's Encrypt over HTTP-01, through the proxy's HTTP listener.</summary>
    Automatic,

    /// <summary>A PKCS#12 (.pfx/.p12) file, reloaded when it changes.</summary>
    Pkcs12File,

    /// <summary>PEM certificate and key files (for example from certbot or acme.sh), reloaded when they change.</summary>
    PemFiles,

    /// <summary>A self-signed certificate generated and kept by the plugin. Useful on the LAN and for testing only.</summary>
    SelfSigned
}
