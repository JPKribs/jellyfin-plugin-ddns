using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// A provider that can also publish the TXT record Let's Encrypt checks for DNS-01, so a certificate can be
/// issued without port 80 and can cover wildcards.
/// </summary>
public interface IAcmeDnsProvider
{
    /// <summary>
    /// Publishes a TXT record.
    /// </summary>
    /// <param name="record">The DDNS record whose credentials and zone to use, with decrypted secrets.</param>
    /// <param name="name">The full TXT name, like <c>_acme-challenge.media.example.com</c>.</param>
    /// <param name="value">The TXT value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it was published, and why not.</returns>
    Task<(bool Ok, string Message)> SetTxtAsync(DNSRecord record, string name, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a TXT record published by <see cref="SetTxtAsync"/>.
    /// </summary>
    /// <param name="record">The DDNS record, with decrypted secrets.</param>
    /// <param name="name">The full TXT name.</param>
    /// <param name="value">The TXT value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when it is gone, including when it was already; false to try again later.</returns>
    Task<bool> RemoveTxtAsync(DNSRecord record, string name, string value, CancellationToken cancellationToken);
}
