using System.Linq;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
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

        // Provider requests carry API keys in headers. Their client follows a redirect only on the same server.
        serviceCollection.AddHttpClient(ProviderHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(ProviderHttp.PrimaryHandler)
            .AddHttpMessageHandler(() => new SameHostRedirectHandler());
        serviceCollection.AddActivityLogger();
        serviceCollection.AddSingleton<IPDetectionService>();
        serviceCollection.AddSingleton<DNSLookupService>();
        serviceCollection.AddSingleton<StatusStoreService>();
        serviceCollection.AddSingleton<DNSUpdateService>();

        // The reverse proxy. It stays stopped until enabled in settings; the hosted service only reads them.
        serviceCollection.AddSingleton(sp => new ProxyPaths(sp.GetRequiredService<IApplicationPaths>()));
        serviceCollection.AddSingleton<CertificateStore>();
        serviceCollection.AddSingleton<Http01ChallengeStore>();
        serviceCollection.AddSingleton<Proxy.Acme.AcmeCertificateService>();
        // Plex-style remote access: forwards on the router, and checks that the name and port work from outside.
        serviceCollection.AddSingleton<Proxy.Router.RouterPortMapper>();
        serviceCollection.AddSingleton<Proxy.Router.ReachabilityChecker>();
        serviceCollection.AddSingleton<Proxy.Router.PublicAddresses>();
        serviceCollection.AddSingleton<ProxyManager>();
        serviceCollection.AddSingleton<JellyfinNetworkOrchestrator>();
        serviceCollection.AddHostedService<ProxyHostedService>();

        // People: who connected from where. Sign-ins come from Jellyfin's events, sessions from a poll, and the
        // address each person used from the proxy.
        serviceCollection.AddSingleton<AccessRecorder>();
        serviceCollection.AddSingleton<GeoIpService>();
        serviceCollection.AddSingleton<ServerLocation>();
        serviceCollection.AddSingleton<BanList>();
        serviceCollection.AddSingleton<BlockFilter>();
        serviceCollection.AddSingleton<PeopleStore>();
        serviceCollection.AddSingleton<PeopleService>();
        serviceCollection.AddScoped<IEventConsumer<AuthenticationRequestEventArgs>, SignInFailedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, SignInSucceededConsumer>();
        serviceCollection.AddHostedService<PeopleHostedService>();
    }
}
