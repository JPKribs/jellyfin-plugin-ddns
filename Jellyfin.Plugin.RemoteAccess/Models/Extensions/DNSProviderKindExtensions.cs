using System;
using Jellyfin.Plugin.DynamicDns.Providers;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// Maps a <see cref="DNSProviderKind"/> to the provider class that implements it. This is the inverse of
/// <see cref="IDNSProvider.Kind"/>.
/// </summary>
public static class DNSProviderKindExtensions
{
    // Every concrete provider lives in the Providers.Implementations namespace and is named {Kind}Provider,
    // so the kind name resolves the implementing type by reflection. No hand kept table to drift out of
    // sync with the enum.
    private const string ProviderNamespace = "Jellyfin.Plugin.DynamicDns.Providers.Implementations";

    /// <summary>
    /// Returns the provider type that implements the given kind. For example
    /// <see cref="DNSProviderKind.Cloudflare"/> resolves to <c>CloudflareProvider</c>.
    /// </summary>
    /// <param name="kind">The provider kind.</param>
    /// <returns>The implementing provider type, or <c>null</c> when no matching class is found.</returns>
    public static Type? ProviderType(this DNSProviderKind kind)
        => typeof(IDNSProvider).Assembly.GetType(ProviderNamespace + "." + kind + "Provider");

    /// <summary>
    /// Returns the simple class name of the provider that implements the given kind, such as
    /// <c>CloudflareProvider</c>.
    /// </summary>
    /// <param name="kind">The provider kind.</param>
    /// <returns>The provider class name, or <c>null</c> when no matching class is found.</returns>
    public static string? ProviderTypeName(this DNSProviderKind kind)
        => kind.ProviderType()?.Name;
}
