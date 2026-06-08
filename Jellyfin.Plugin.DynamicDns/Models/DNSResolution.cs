using System.Collections.Generic;
using System.Net;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The addresses a hostname currently resolves to in DNS, or a failed lookup. The update cycle uses this
/// to decide whether a record already serves the detected public IP, so a record is left alone only when
/// live DNS already agrees rather than when a locally stored value does.
/// </summary>
public sealed class DNSResolution
{
    private readonly IReadOnlyList<IPAddress> _addresses;

    private DNSResolution(bool succeeded, IReadOnlyList<IPAddress> addresses)
    {
        Succeeded = succeeded;
        _addresses = addresses;
    }

    /// <summary>Gets a value indicating whether the lookup completed and returned an answer.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets a failed or unresolved lookup, which serves no address and so forces an update.</summary>
    public static DNSResolution Failed { get; } = new(false, new List<IPAddress>());

    /// <summary>
    /// Builds a successful resolution from the addresses a hostname resolved to.
    /// </summary>
    /// <param name="addresses">The resolved addresses.</param>
    /// <returns>A resolution that serves those addresses.</returns>
    public static DNSResolution Resolved(IReadOnlyList<IPAddress> addresses) => new(true, addresses);

    /// <summary>
    /// Returns whether DNS currently serves the given address for the hostname.
    /// </summary>
    /// <param name="ipText">The address to look for.</param>
    /// <returns>True when the lookup succeeded and the address is among the resolved set.</returns>
    public bool Serves(string? ipText)
    {
        if (!Succeeded || string.IsNullOrEmpty(ipText) || !IPAddress.TryParse(ipText, out var target))
        {
            return false;
        }

        foreach (var address in _addresses)
        {
            if (address.Equals(target))
            {
                return true;
            }
        }

        return false;
    }
}
