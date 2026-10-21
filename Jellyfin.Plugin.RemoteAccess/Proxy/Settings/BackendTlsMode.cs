namespace Jellyfin.Plugin.DynamicDns.Proxy.Settings;

/// <summary>
/// How an HTTPS backend's certificate is checked.
/// </summary>
public enum BackendTlsMode
{
    /// <summary>Normal certificate validation.</summary>
    Verify,

    /// <summary>Accept only the certificate with the pinned SHA-256 thumbprint (for self-signed backends).</summary>
    Pinned,

    /// <summary>Accept any certificate. Only for backends on a trusted network.</summary>
    Skip
}
