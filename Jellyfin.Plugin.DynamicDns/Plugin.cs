using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns;

/// <summary>
/// Main plugin entry point for Dynamic DNS.
/// </summary>
public class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation("Dynamic DNS plugin initialized");
    }

    /// <inheritdoc />
    public override string Name => "Dynamic DNS";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8150dce5-6153-4924-a985-a6927be95baa");

    /// <inheritdoc />
    public override string Description => "Keep your DNS records pointed at your server's current public IP.";

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        // Tab 1: Domains (the dashboard menu entry).
        yield return new PluginPageInfo
        {
            Name = "ddns_domains",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_domains.html",
            MenuSection = "server",
            DisplayName = "Dynamic DNS",
            EnableInMainMenu = false
        };

        yield return new PluginPageInfo
        {
            Name = "ddns_domains.js",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_domains.js"
        };

        // Tab 2: Address.
        yield return new PluginPageInfo
        {
            Name = "ddns_address",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_address.html"
        };

        yield return new PluginPageInfo
        {
            Name = "ddns_address.js",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_address.js"
        };

        // Shared base CSS and JS compiled in from the JPKribs.Jellyfin.Base package.
        foreach (var page in GetSharedPages("ddns"))
        {
            yield return page;
        }
    }
}
