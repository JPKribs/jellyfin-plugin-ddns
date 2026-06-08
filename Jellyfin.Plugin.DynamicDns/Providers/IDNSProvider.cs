using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Pushes a record's configured IP address(es) to a specific DNS provider.
/// </summary>
/// <remarks>
/// To add a provider: (1) add a value to <see cref="DNSProviderKind"/>. (2) add a class deriving from
/// <see cref="DNSProviderBase"/> that returns that <see cref="Kind"/> and implements
/// <see cref="UpdateAsync"/>. Registration is automatic (the assembly is scanned), and per-family
/// providers should delegate the A/AAAA loop to <see cref="DNSProviderBase.ApplyPerFamilyAsync"/>.
/// Override <see cref="Label"/>, <see cref="Hint"/>, and <see cref="Fields"/> so the dashboard renders the
/// provider with no JS change.
/// </remarks>
public interface IDNSProvider
{
    /// <summary>Gets the provider this implementation handles.</summary>
    DNSProviderKind Kind { get; }

    /// <summary>Gets the friendly name shown in the provider dropdown.</summary>
    string Label { get; }

    /// <summary>Gets the short hint shown under the provider dropdown, describing what each field holds.</summary>
    string Hint { get; }

    /// <summary>Gets the fields this provider uses and their labels, so the dashboard shows only what it needs.</summary>
    ProviderFields Fields { get; }

    /// <summary>
    /// Updates the record so its A/AAAA entries match the detected address(es).
    /// </summary>
    /// <param name="record">The record to update.</param>
    /// <param name="ip">The detected public IP address(es).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the attempt.</returns>
    Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken);
}
