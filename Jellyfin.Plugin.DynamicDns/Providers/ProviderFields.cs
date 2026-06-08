using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Providers;

/// <summary>
/// Which record fields a provider uses and the label to show for each, so the dashboard renders only the
/// inputs a provider needs. A null label hides that field. This lives on the provider so the UI is driven
/// from one place per provider rather than a separate table.
/// </summary>
public sealed class ProviderFields
{
    /// <summary>Gets the hostname field label, or null to hide it.</summary>
    public string? Hostname { get; init; }

    /// <summary>Gets the login field label, or null to hide it.</summary>
    public string? Login { get; init; }

    /// <summary>Gets the password field label, or null to hide it.</summary>
    public string? Password { get; init; }

    /// <summary>Gets the zone field label, or null to hide it.</summary>
    public string? Zone { get; init; }

    /// <summary>Gets a value indicating whether the optional server override field is shown.</summary>
    public bool Server { get; init; }

    /// <summary>Gets a value indicating whether the optional TTL field is shown.</summary>
    public bool Ttl { get; init; }

    /// <summary>Gets the advanced flags this provider supports, such as wildcard, static, mx, backupmx.</summary>
    public IReadOnlyList<string> Advanced { get; init; } = Array.Empty<string>();
}
