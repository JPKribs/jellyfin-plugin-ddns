namespace Jellyfin.Plugin.DynamicDns.Proxy.Acme;

/// <summary>
/// What the certificate planner wants done.
/// </summary>
public enum AcmeAction
{
    /// <summary>The certificate is current; nothing to do.</summary>
    None,

    /// <summary>There is no certificate for these names yet; get one.</summary>
    Issue,

    /// <summary>The certificate is close to expiring; get a new one.</summary>
    Renew,

    /// <summary>Recent attempts failed; wait before trying again.</summary>
    Wait,

    /// <summary>There is no name Let's Encrypt can check over HTTP-01, such as only wildcards.</summary>
    NoNames
}
