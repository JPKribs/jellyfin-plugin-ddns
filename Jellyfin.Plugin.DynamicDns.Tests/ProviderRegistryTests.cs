using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Guards the provider registry. Every provider must expose a unique <see cref="DNSProviderKind"/>.
/// A duplicate would make the runtime's <c>ToDictionary(p =&gt; p.Kind)</c> throw at startup. This also
/// proves every discovered provider actually constructs through DI.
/// </summary>
public class ProviderRegistryTests
{
    [Fact]
    public void AllProviders_ConstructAndHaveDistinctKind()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(StubHttp.Always(HttpStatusCode.OK, string.Empty));

        // Same discovery the runtime registrator uses.
        var providerTypes = typeof(IDNSProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t))
            .ToList();
        foreach (var type in providerTypes)
        {
            services.AddSingleton(typeof(IDNSProvider), type);
        }

        using var provider = services.BuildServiceProvider();
        var kinds = provider.GetServices<IDNSProvider>().Select(p => p.Kind).ToList();

        Assert.NotEmpty(kinds);
        Assert.Equal(providerTypes.Count, kinds.Count);                 // all constructed
        Assert.Equal(kinds.Count, kinds.Distinct().Count());           // all unique
    }

    [Fact]
    public void EveryProviderKind_ResolvesToItsImplementingType()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(StubHttp.Always(HttpStatusCode.OK, string.Empty));

        var providerTypes = typeof(IDNSProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t))
            .ToList();
        foreach (var type in providerTypes)
        {
            services.AddSingleton(typeof(IDNSProvider), type);
        }

        using var provider = services.BuildServiceProvider();

        // The helper must round trip: a provider's Kind resolves back to that provider's own type.
        foreach (var instance in provider.GetServices<IDNSProvider>())
        {
            Assert.Equal(instance.GetType(), instance.Kind.ProviderType());
        }
    }

    [Fact]
    public void EveryProvider_ExposesCompleteMetadata()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(StubHttp.Always(HttpStatusCode.OK, string.Empty));

        foreach (var type in typeof(IDNSProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t)))
        {
            services.AddSingleton(typeof(IDNSProvider), type);
        }

        using var provider = services.BuildServiceProvider();

        // The dashboard is driven entirely from this metadata, so every provider must label itself, give a
        // hint, and expose at least one credential field.
        foreach (var instance in provider.GetServices<IDNSProvider>())
        {
            Assert.False(string.IsNullOrWhiteSpace(instance.Label), instance.Kind + " has no Label");
            Assert.False(string.IsNullOrWhiteSpace(instance.Hint), instance.Kind + " has no Hint");
            var f = instance.Fields;
            Assert.NotNull(f);
            var hasField = f.Hostname is not null || f.Login is not null || f.Password is not null || f.Zone is not null;
            Assert.True(hasField, instance.Kind + " exposes no input fields");
        }
    }
}
