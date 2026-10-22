using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.DynamicDns.Api;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // The providers built into this assembly, for the generic save to tell which ones ask for a hostname.
    private static readonly Lazy<IReadOnlyList<IDNSProvider>> _builtInProviders = new(LoadBuiltInProviders);

    private readonly Lazy<SecretProtector> _secrets;
    private readonly Lazy<StatusStoreService> _statusStore;
    private readonly IConfigurationManager? _configurationManager;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configurationManager">Jellyfin's configuration manager, used to restore its network settings on uninstall.</param>
    /// <param name="statusStore">The runtime status store the update run uses, so the generic save resets a retargeted record's status in the same store.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        IConfigurationManager? configurationManager = null,
        StatusStoreService? statusStore = null)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);
        _configurationManager = configurationManager;
        _logger = logger;
        _secrets = new Lazy<SecretProtector>(() => StableSecretProtection.CreateProtector(applicationPaths, SecretsName, logger));

        // Jellyfin builds the plugin from the same container the plugin's services are registered in, so the
        // shared store normally arrives here. A store of its own over the same file is the fallback.
        _statusStore = new Lazy<StatusStoreService>(() => statusStore ?? new StatusStoreService(applicationPaths, NullLogger<StatusStoreService>.Instance));
        ProtectFolders(applicationPaths, logger);
        logger.LogInformation("Dynamic DNS plugin initialized");
    }

    // The key ring that encrypts saved credentials, and the folder with the certificate keys, Let's Encrypt
    // account and visitor history, are kept to the account Jellyfin runs as. A failure is logged, not fatal:
    // the plugin still works, only less privately.
    private static void ProtectFolders(IApplicationPaths paths, ILogger logger)
    {
        var folders = new[] { StableSecretProtection.KeyDirectory(paths, SecretsName), new ProxyPaths(paths).DataDirectory };

        // Checked before locking: a folder this account cannot open is one it cannot narrow either, and the
        // check is what tells the administrator how to give it back.
        var problem = FolderAccess.Check(folders);
        if (problem.Length > 0)
        {
            logger.LogError("{Problem}", problem);
            return;
        }

        KeyRingProtection.Protect(folders[0], logger);
        foreach (var folder in folders)
        {
            try
            {
                Proxy.SecretFile.ProtectFolder(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or System.Security.SecurityException)
            {
                logger.LogWarning("Could not restrict access to {Folder}: {Message}", folder, ex.Message);
            }
        }
    }

    /// <summary>
    /// Gets the protector that encrypts record credentials at rest. The generic configuration endpoint
    /// (handled in <see cref="OnConfigurationSaving"/>) uses it, and the DI protector built under the same
    /// name shares its key ring.
    /// </summary>
    public SecretProtector Secrets => _secrets.Value;

    /// <summary>Gets the folder of the key ring that encrypts saved credentials.</summary>
    public string KeyRingDirectory => StableSecretProtection.KeyDirectory(ApplicationPaths, SecretsName);

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
        incoming.Records ??= new();

        // Checked before anything is changed: XML serialization fails partway on an unknown provider and
        // leaves a truncated configuration file behind.
        if (incoming.Records.Any(r => r is null || !Enum.IsDefined(r.Provider)))
        {
            throw new ArgumentException("A record has an unknown provider.", nameof(incoming));
        }

        // Runtime status lives in the status store, so whatever a client sent for it is dropped first.
        DynamicDNSController.ClearRuntimeStatus(incoming);
        if (XmlText.FindInvalid(incoming) is { } bad)
        {
            throw new ArgumentException($"{bad} holds a character that cannot be saved.", nameof(incoming));
        }

        if (DynamicDNSController.MissingHostname(incoming.Records, _builtInProviders.Value))
        {
            throw new ArgumentException("Every record needs a hostname.", nameof(incoming));
        }

        incoming.IPv4DetectionUrl ??= string.Empty;
        incoming.IPv6DetectionUrl ??= string.Empty;
        DynamicDNSController.ClampSettings(incoming);

        // Status, pending TXT records and the audit all go by record id, so a record saved here without
        // one, or with another's, gets its own, as the dashboard's save does.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var needsId = incoming.Records.Where(r => string.IsNullOrEmpty(r.Id) || !ids.Add(r.Id)).ToList();
        foreach (var record in needsId)
        {
            record.Id = Guid.NewGuid().ToString("N");
        }

        // A client that saved the dashboard's redacted configuration here sends its placeholders. The kept one
        // takes the stored value of the same record, still sent to the same place, and the clear one empties it.
        var previous = new Dictionary<string, DNSRecord>(StringComparer.Ordinal);
        foreach (var record in (stored?.Records ?? new List<DNSRecord>()).Where(r => r is not null && !string.IsNullOrEmpty(r.Id)))
        {
            previous.TryAdd(record.Id, record);
        }

        foreach (var record in incoming.Records)
        {
            var prior = previous.TryGetValue(record.Id, out var match) && RecordMerge.SameDestination(match, record) ? match : null;
            record.Login = DynamicDNSController.MapPlaceholder(record.Login, prior?.Login);
            record.Password = DynamicDNSController.MapPlaceholder(record.Password, prior?.Password);
        }

        CredentialEncryption.DropForeignCiphertext(incoming.Records, stored?.Records);
        CredentialEncryption.ProtectAll(incoming.Records, Secrets);

        // The dashboard save audits in its own endpoint, and this generic path is the only other
        // place records can change. Diffing after ProtectAll compares stored forms, so a ciphertext
        // echoed back by a client is unchanged while a newly entered credential is a fresh
        // encryption. The stored configuration can be null on the very first save before any file exists.
        ConfigurationAudit.LogRecordChanges(ActivityLogger.Current, stored?.Records, incoming.Records);

        // The proxy settings get the same treatment as the dashboard's proxy endpoint: the certificate
        // password is encrypted, values are normalized, and an invalid configuration is refused.
        // The People settings get the same limits as their own endpoint.
        incoming.People ??= new();
        incoming.People.HistoryDays = Math.Clamp(incoming.People.HistoryDays, 1, 3650);
        if (!Enum.IsDefined(incoming.People.Location))
        {
            throw new ArgumentException("Location has an unknown value.", nameof(incoming));
        }

        incoming.Proxy ??= new();
        incoming.Proxy.Certificate ??= new();
        var rawHostnames = incoming.Proxy.Hostnames;
        var storedCertificate = stored?.Proxy?.Certificate;
        var sameFile = storedCertificate is not null
            && incoming.Proxy.Certificate.Mode == storedCertificate.Mode
            && string.Equals(incoming.Proxy.Certificate.Path, storedCertificate.Path, StringComparison.Ordinal)
            && string.Equals(incoming.Proxy.Certificate.KeyPath, storedCertificate.KeyPath, StringComparison.Ordinal);
        incoming.Proxy.Certificate.Password = Secrets.Protect(
            CredentialEncryption.OwnOrEmpty(incoming.Proxy.Certificate.Password, sameFile ? storedCertificate!.Password : null));
        ProxySettingsValidator.Normalize(incoming.Proxy);
        var errors = ProxySettingsValidator.Validate(incoming.Proxy, rawHostnames);
        if (!incoming.Proxy.Enabled && IsTakeoverActive())
        {
            errors.Add("Undo Trust the Proxy before turning the proxy off, so Jellyfin's remote access goes back to how it was.");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(incoming));
        }

        // As on the dashboard save: a record now pointed somewhere else starts its status over, so it does not
        // inherit the old target's backoff and last pushed addresses. Done last, once nothing can refuse the save.
        _statusStore.Value.RemoveRecords(DynamicDNSController.RetargetedIds(stored?.Records, incoming.Records));
    }

    // Builds one instance of every provider in this assembly to read its fields. Building one only stores
    // what it is given, so no client or logger is needed to ask which fields it shows.
    private static List<IDNSProvider> LoadBuiltInProviders()
    {
        var providers = new List<IDNSProvider>();
        var types = typeof(Plugin).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDNSProvider).IsAssignableFrom(t));
        foreach (var type in types)
        {
            var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
            if (constructor is null)
            {
                continue;
            }

            var arguments = constructor.GetParameters().Select(p => PlaceholderArgument(p.ParameterType)).ToArray();
            try
            {
                providers.Add((IDNSProvider)constructor.Invoke(arguments));
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or MemberAccessException)
            {
                // A provider that cannot be built this way is treated as asking for a hostname.
            }
        }

        return providers;
    }

    private static object? PlaceholderArgument(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()));
        }

        return type == typeof(ILogger) ? NullLogger.Instance : null;
    }

    /// <summary>
    /// Puts Jellyfin's own network settings back if the proxy had taken over its ports, so uninstalling the
    /// plugin cannot leave Jellyfin listening only on loopback.
    /// </summary>
    public override void OnUninstalling()
    {
        if (_configurationManager is not null)
        {
            try
            {
                JellyfinNetworkOrchestrator.RestoreForUninstall(new ProxyPaths(ApplicationPaths), _configurationManager, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogError(ex, "Could not restore Jellyfin's network settings during uninstall");
            }
        }

        base.OnUninstalling();
    }

    /// <summary>
    /// Whether the proxy currently manages Jellyfin's network settings.
    /// </summary>
    /// <returns>True when a takeover snapshot exists.</returns>
    internal bool IsTakeoverActive()
    {
        var state = new JsonFileStore<TakeoverState>(new ProxyPaths(ApplicationPaths).StateFile).Load();
        return state.Snapshot is not null;
    }

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        // The Dashboard is the first tab and the plugin's only sidebar entry. Jellyfin's Settings button on the Plugins
        // page opens the sidebar page too, so every way in lands here, and the tabs lead to the settings.
        yield return new PluginPageInfo
        {
            Name = "ddns_dashboard",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_dashboard.html",
            MenuSection = "server",
            DisplayName = "Remote Access",
            EnableInMainMenu = true,
            MenuIcon = "public"
        };

        yield return new PluginPageInfo
        {
            Name = "ddns_dashboard.js",
            EmbeddedResourcePath = $"{ns}.Configuration.ddns_dashboard.js"
        };

        // Tab 2: Domains.
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

        // Tab 3: Address.
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

        // Tabs 4 to 6: remote access through the built-in proxy.
        foreach (var name in new[] { "ddns_remote", "ddns_services", "ddns_advanced" })
        {
            yield return new PluginPageInfo
            {
                Name = name,
                EmbeddedResourcePath = $"{ns}.Configuration.{name}.html"
            };

            yield return new PluginPageInfo
            {
                Name = name + ".js",
                EmbeddedResourcePath = $"{ns}.Configuration.{name}.js"
            };
        }

        // Modules the pages import: the tab bar, the hostname picker, the world map and the shared page styles.
        foreach (var module in new[] { "ddns_tabs.js", "ddns_hostnames.js", "ddns_worldmap.js", "ddns_common.css" })
        {
            yield return new PluginPageInfo
            {
                Name = module,
                EmbeddedResourcePath = $"{ns}.Configuration.{module}"
            };
        }

        foreach (var page in GetSharedPages("ddns"))
        {
            yield return page;
        }
    }
}
