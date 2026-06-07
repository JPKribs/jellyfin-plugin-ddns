using System.Linq;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns;

/// <summary>
/// Registers plugin services with the Jellyfin DI container. Every concrete <see cref="IDnsProvider"/>
/// in this assembly is discovered and registered automatically, so adding a provider needs no wiring here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        var providers = typeof(PluginServiceRegistrator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDnsProvider).IsAssignableFrom(t));
        foreach (var provider in providers)
        {
            serviceCollection.AddSingleton(typeof(IDnsProvider), provider);
        }

        serviceCollection.AddSingleton(sp => new SecretProtector(
            "Jellyfin.Plugin.DynamicDns.Secrets.v1",
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.DynamicDns.SecretProtector"),
            sp.GetService<IDataProtectionProvider>()));
        serviceCollection.AddSingleton<IpDetectionService>();
        serviceCollection.AddSingleton<DnsUpdateService>();
    }
}
