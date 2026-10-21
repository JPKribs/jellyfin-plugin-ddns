namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// How Let's Encrypt checks that the addresses belong to this server.
/// </summary>
public enum AcmeMethod
{
    /// <summary>A token fetched over port 80. Needs port 80 forwarded; cannot cover wildcards.</summary>
    Http,

    /// <summary>A TXT record published through the DDNS provider. Needs no open port and covers wildcards.</summary>
    Dns
}
