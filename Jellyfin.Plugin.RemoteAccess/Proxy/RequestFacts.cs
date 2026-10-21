using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The parts of a request the policy looks at.
/// </summary>
/// <param name="Local">Whether the client is on the LAN or a trusted network.</param>
/// <param name="IsHttps">Whether the request arrived on the HTTPS listener.</param>
/// <param name="Host">The Host header, without port.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, including Jellyfin's base URL.</param>
/// <param name="Query">The raw query string.</param>
/// <param name="TreatedAsInternet">Whether a local client asking for a public name gets the internet's path rules.</param>
public readonly record struct RequestFacts(bool Local, bool IsHttps, string Host, string Method, PathString Path, QueryString Query, bool TreatedAsInternet = false)
{
    /// <summary>Gets a value indicating whether the internet's path rules apply: the user list, setup and Local-only Services.</summary>
    public bool InternetRules => !Local || TreatedAsInternet;
}
