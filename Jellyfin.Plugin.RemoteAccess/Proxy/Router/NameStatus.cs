using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Whether the public name people use points at this server.
/// </summary>
public sealed class NameStatus
{
    /// <summary>Gets or sets the name checked: the first exact public hostname, empty when there is none.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the name resolves to this server's public address.</summary>
    public bool Resolves { get; set; }

    /// <summary>Gets or sets the addresses the name resolved to.</summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>Gets or sets this server's public address the name was compared with, empty while unknown.</summary>
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets what went wrong, empty when nothing did.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Copies the status, so a caller never holds the cached one.
    /// </summary>
    /// <returns>The copy.</returns>
    public NameStatus Copy()
    {
        var copy = (NameStatus)MemberwiseClone();
        copy.Addresses = new List<string>(Addresses);
        return copy;
    }
}
