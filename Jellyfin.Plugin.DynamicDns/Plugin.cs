using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
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
    private readonly Lazy<SecretProtector> _secrets;
    private bool _secretsPlaintext;

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
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = new Lazy<SecretProtector>(() => CreateSecretProtector(applicationPaths, logger));
        logger.LogInformation("Dynamic DNS plugin initialized");
    }

    /// <summary>
    /// Gets the protector that encrypts record credentials at rest. The plugin owns the single instance
    /// so the generic configuration endpoint (handled in <see cref="UpdateConfiguration"/>) and the DI
    /// consumers share one protector and one key ring.
    /// </summary>
    public SecretProtector Secrets => _secrets.Value;

    /// <summary>
    /// Gets a value indicating whether credentials are being stored in plaintext because the Data
    /// Protection key store could not be initialized. Surfaced on the dashboard so the degradation is
    /// visible to the administrator, not just the server log.
    /// </summary>
    public bool SecretsPlaintext
    {
        get
        {
            _ = _secrets.Value;
            return _secretsPlaintext;
        }
    }

    /// <inheritdoc />
    public override string Name => "Dynamic DNS";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8150dce5-6153-4924-a985-a6927be95baa");

    /// <inheritdoc />
    public override string Description => "Keep your DNS records pointed at your server's current public IP.";

    /// <summary>
    /// Encrypts incoming credentials before the configuration is persisted. The dashboard's own save
    /// endpoint already encrypts, but Jellyfin's generic plugin configuration endpoint bypasses it, and
    /// without this hook a secret saved there would sit in the XML in plaintext until the next update
    /// run's backstop caught it.
    /// </summary>
    /// <param name="configuration">The incoming configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            CredentialEncryption.ProtectAll(config.Records, Secrets);

            // The dashboard save audits in its own endpoint, and this generic path is the only other
            // place records can change. Diffing after ProtectAll compares stored forms, so a ciphertext
            // echoed back by a client is unchanged while a newly entered credential is a fresh
            // encryption. Configuration can be null on the very first save before any file exists.
            ConfigurationAudit.LogRecordChanges(ActivityLogger.Instance, Configuration?.Records, config.Records);
        }

        base.UpdateConfiguration(configuration);
    }

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

    private SecretProtector CreateSecretProtector(IApplicationPaths paths, ILogger logger)
    {
        var keyDirectory = Path.Join(paths.PluginConfigurationsPath, "Jellyfin.Plugin.DynamicDns.Keys");
        var provider = StableSecretProtection.Build(keyDirectory, logger);
        _secretsPlaintext = provider is null;
        return new SecretProtector("Jellyfin.Plugin.DynamicDns.Secrets.v1", logger, provider);
    }
}
