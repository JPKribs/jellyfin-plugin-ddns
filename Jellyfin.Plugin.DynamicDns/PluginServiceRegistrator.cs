using System.Linq;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DynamicDns;

/// <summary>
/// Registers plugin services with the Jellyfin DI container. Every concrete <see cref="IDNSProvider"/>
/// in this assembly is discovered and registered automatically, so adding a provider needs no wiring here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        var providers = typeof(PluginServiceRegistrator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t));
        foreach (var provider in providers)
        {
            serviceCollection.AddSingleton(typeof(IDNSProvider), provider);
        }

        // Encrypts DNS provider credentials at rest. Every protector built under the same name shares one
        // key ring, so this one and the Plugin's (used by the generic configuration endpoint) read the same
        // secrets. Keys live in a fixed directory under the Jellyfin data folder with a pinned application
        // name. Otherwise a host launch-context change (app update, desktop-app vs service, Docker) shifts the
        // Data Protection discriminator (and default key location) and every stored secret fails to decrypt.
        serviceCollection.AddPluginSecretProtector(Plugin.SecretsName);
        serviceCollection.AddActivityLogger();
        serviceCollection.AddSingleton<IPDetectionService>();
        serviceCollection.AddSingleton<DNSLookupService>();
        serviceCollection.AddSingleton<StatusStoreService>();
        serviceCollection.AddSingleton<DNSUpdateService>();
    }
}
