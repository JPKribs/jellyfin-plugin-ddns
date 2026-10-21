using System;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.DynamicDns.Api;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The plugin's whole service graph resolves. A missing registration stops Jellyfin from starting at all, so it
/// must fail here rather than on a server.
/// </summary>
public class ServiceRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();

        // What Jellyfin itself provides to plugins.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Substitute.For<IApplicationPaths>());
        services.AddSingleton(Substitute.For<IConfigurationManager>());
        services.AddSingleton(Substitute.For<INetworkManager>());
        services.AddSingleton(Substitute.For<IServerApplicationHost>());
        services.AddSingleton(Substitute.For<ISystemManager>());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        services.AddSingleton(Substitute.For<ISessionManager>());
        services.AddSingleton(Substitute.For<IHttpClientFactory>());
        services.AddSingleton(Substitute.For<IActivityManager>());

        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
        return services;
    }

    [Fact]
    public void EveryRegisteredServiceResolves()
    {
        // ValidateOnBuild checks every registration can be constructed, without constructing singletons.
        using var provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider);
    }

    [Fact]
    public void EveryControllerResolves()
    {
        var services = Services();
        var controllers = typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            services.AddTransient(controller);
        }

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Contains(typeof(ProxyController), controllers);
        Assert.Contains(typeof(PeopleController), controllers);
    }
}
