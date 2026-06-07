using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Pushes a record's configured IP address(es) to a specific DNS provider.
/// </summary>
/// <remarks>
/// To add a provider: (1) add a value to <see cref="DnsProviderKind"/>; (2) add a class deriving from
/// <see cref="DnsProviderBase"/> that returns that <see cref="Kind"/> and implements
/// <see cref="UpdateAsync"/>. Registration is automatic (the assembly is scanned), and per-family
/// providers should delegate the A/AAAA loop to <see cref="DnsProviderBase.ApplyPerFamilyAsync"/>.
/// Add a UI hint for the new kind in <c>Configuration/ddns_config.js</c>.
/// </remarks>
public interface IDnsProvider
{
    /// <summary>Gets the provider this implementation handles.</summary>
    DnsProviderKind Kind { get; }

    /// <summary>
    /// Updates the record so its A/AAAA entries match the detected address(es).
    /// </summary>
    /// <param name="record">The record to update.</param>
    /// <param name="ip">The detected public IP address(es).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the attempt.</returns>
    Task<DnsUpdateResult> UpdateAsync(DnsRecord record, DetectedIp ip, CancellationToken cancellationToken);
}
