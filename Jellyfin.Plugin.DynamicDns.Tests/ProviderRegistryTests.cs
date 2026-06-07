using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Guards the provider registry. Every provider must expose a unique <see cref="DnsProviderKind"/>;
/// a duplicate would make the runtime's <c>ToDictionary(p =&gt; p.Kind)</c> throw at startup. This also
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
        var providerTypes = typeof(IDnsProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDnsProvider).IsAssignableFrom(t))
            .ToList();
        foreach (var type in providerTypes)
        {
            services.AddSingleton(typeof(IDnsProvider), type);
        }

        using var provider = services.BuildServiceProvider();
        var kinds = provider.GetServices<IDnsProvider>().Select(p => p.Kind).ToList();

        Assert.NotEmpty(kinds);
        Assert.Equal(providerTypes.Count, kinds.Count);                 // all constructed
        Assert.Equal(kinds.Count, kinds.Distinct().Count());           // all unique
    }
}
