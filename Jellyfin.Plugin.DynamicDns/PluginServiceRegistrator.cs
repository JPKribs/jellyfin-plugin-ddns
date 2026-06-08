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

        // Encrypts DNS provider credentials at rest. Keys live in a fixed directory
        // under the Jellyfin data folder with a pinned application name — otherwise a
        // host launch-context change (app update, desktop-app vs service, Docker)
        // shifts the Data Protection discriminator (and default key location) and
        // every stored secret fails to decrypt. See StableSecretProtection.
        serviceCollection.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.DynamicDns.SecretProtector");
            IDataProtectionProvider? provider = null;

            var paths = sp.GetService<MediaBrowser.Common.Configuration.IApplicationPaths>();
            if (paths is not null)
            {
                var keyDirectory = System.IO.Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.DynamicDns.Keys");
                provider = StableSecretProtection.Build(keyDirectory, logger);
            }

            return new SecretProtector("Jellyfin.Plugin.DynamicDns.Secrets.v1", logger, provider);
        });
        serviceCollection.AddSingleton<IpDetectionService>();
        serviceCollection.AddSingleton<DnsUpdateService>();
    }
}
