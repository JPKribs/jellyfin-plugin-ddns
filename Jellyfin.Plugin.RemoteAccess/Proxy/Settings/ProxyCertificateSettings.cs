namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// Where the proxy's TLS certificate comes from.
/// </summary>
public class ProxyCertificateSettings
{
    /// <summary>Gets or sets the certificate source.</summary>
    public CertificateMode Mode { get; set; } = CertificateMode.Automatic;

    /// <summary>Gets or sets the PKCS#12 (.pfx) file, or the PEM certificate (chain) file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the PEM private key file. Only used in <see cref="CertificateMode.PemFiles"/> mode.</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the PKCS#12 password, or the PEM key password. Stored encrypted.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the administrator accepted the Let's Encrypt terms.
    /// Nothing is requested from Let's Encrypt until they have.</summary>
    public bool AcceptTerms { get; set; }
}
