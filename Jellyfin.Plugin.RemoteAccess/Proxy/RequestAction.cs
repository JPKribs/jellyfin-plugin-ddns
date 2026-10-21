namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// What the proxy does with a request before routing it.
/// </summary>
public enum RequestAction
{
    /// <summary>Forward it.</summary>
    Forward,

    /// <summary>Close the connection without answering.</summary>
    Drop,

    /// <summary>Answer 403.</summary>
    Forbidden,

    /// <summary>Redirect to HTTPS.</summary>
    Redirect,

    /// <summary>Answer an ACME HTTP-01 challenge.</summary>
    Challenge
}
