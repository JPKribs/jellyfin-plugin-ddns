using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.Configuration;
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
    /// <summary>
    /// The stable name the secret key folder, Data Protection application name, and purpose derive from.
    /// Changing it makes every stored credential unreadable.
    /// </summary>
    internal const string SecretsName = "Jellyfin.Plugin.DynamicDns";

    private readonly Lazy<SecretProtector> _secrets;

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
        _secrets = new Lazy<SecretProtector>(() => StableSecretProtection.CreateProtector(applicationPaths, SecretsName, logger));
        logger.LogInformation("Dynamic DNS plugin initialized");
    }

    /// <summary>
    /// Gets the protector that encrypts record credentials at rest. The generic configuration endpoint
    /// (handled in <see cref="OnConfigurationSaving"/>) uses it, and the DI protector built under the same
    /// name shares its key ring.
    /// </summary>
    public SecretProtector Secrets => _secrets.Value;

    /// <summary>
    /// Gets a value indicating whether credentials are being stored in plaintext because the Data
    /// Protection key store could not be initialized. Surfaced on the dashboard so the degradation is
    /// visible to the administrator, not just the server log.
    /// </summary>
    public bool SecretsPlaintext => !Secrets.IsAvailable;

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
    /// <param name="incoming">The incoming configuration.</param>
    /// <param name="stored">The configuration currently in effect.</param>
    protected override void OnConfigurationSaving(PluginConfiguration incoming, PluginConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        CredentialEncryption.ProtectAll(incoming.Records, Secrets);

        // The dashboard save audits in its own endpoint, and this generic path is the only other
        // place records can change. Diffing after ProtectAll compares stored forms, so a ciphertext
        // echoed back by a client is unchanged while a newly entered credential is a fresh
        // encryption. The stored configuration can be null on the very first save before any file exists.
        ConfigurationAudit.LogRecordChanges(ActivityLogger.Current, stored?.Records, incoming.Records);
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
            EnableInMainMenu = false,
            MenuIcon = "dns"
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

        foreach (var page in GetSharedPages("ddns"))
        {
            yield return page;
        }
    }
}
