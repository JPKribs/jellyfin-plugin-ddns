using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Every registered provider must reject a record that has no hostname or credentials before it makes any
/// network call. The HTTP factory throws if a client is ever created, so a provider that skips its guards
/// fails loudly and names itself.
/// </summary>
public class AllProvidersValidationTests
{
    [Fact]
    public async Task EveryProvider_RejectsEmptyRecord_WithoutNetwork()
    {
        var noNetwork = Substitute.For<IHttpClientFactory>();
        noNetwork.CreateClient(Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("attempted to create an HTTP client"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(noNetwork);
        foreach (var type in typeof(IDNSProvider).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t)))
        {
            services.AddSingleton(typeof(IDNSProvider), type);
        }

        using var sp = services.BuildServiceProvider();
        var providers = sp.GetServices<IDNSProvider>().ToList();
        Assert.NotEmpty(providers);

        var ip = new DetectedIP { IPv4 = "1.2.3.4", IPv6 = "2001:db8::1" };
        foreach (var p in providers)
        {
            var empty = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = true };

            DNSUpdateResult result;
            try
            {
                result = await p.UpdateAsync(empty, ip, CancellationToken.None);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(p.Kind + " attempted a network call before validating: " + ex.Message);
            }

            Assert.False(result.Success, p.Kind + " accepted an empty record");
        }
    }
}
