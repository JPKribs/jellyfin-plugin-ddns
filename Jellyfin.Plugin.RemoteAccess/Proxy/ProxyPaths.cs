using System;
using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Where the proxy keeps its files, beside the plugin's configuration.
/// </summary>
public sealed class ProxyPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyPaths"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    public ProxyPaths(IApplicationPaths paths)
        : this(Path.Join(paths?.PluginConfigurationsPath ?? throw new ArgumentNullException(nameof(paths)), "Jellyfin.Plugin.DynamicDns.Proxy"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyPaths"/> class rooted at a directory.
    /// </summary>
    /// <param name="dataDirectory">The directory.</param>
    public ProxyPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>Gets the proxy's data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the orchestrator state file.</summary>
    public string StateFile => Path.Join(DataDirectory, "network-state.json");

    /// <summary>Gets the directory holding the Let's Encrypt account key, the issued certificate and its state.</summary>
    public string AcmeDirectory => Path.Join(DataDirectory, "acme");

    /// <summary>Gets the issued certificate chain, as PEM.</summary>
    public string AcmeChainFile => Path.Join(AcmeDirectory, "fullchain.pem");

    /// <summary>Gets the issued certificate's private key, as PEM.</summary>
    public string AcmeKeyFile => Path.Join(AcmeDirectory, "privkey.pem");

    /// <summary>Gets the Let's Encrypt account key, as PEM.</summary>
    public string AcmeAccountKeyFile => Path.Join(AcmeDirectory, "account.pem");

    /// <summary>Gets the record of issuance attempts.</summary>
    public string AcmeStateFile => Path.Join(AcmeDirectory, "state.json");

    /// <summary>Gets the self-signed certificate the proxy serves, issued from <see cref="SelfSignedRootFile"/>.</summary>
    public string SelfSignedFile => Path.Join(DataDirectory, "selfsigned.pfx");

    /// <summary>Gets the root the self-signed certificate is issued from, with its private key.</summary>
    public string SelfSignedRootFile => Path.Join(DataDirectory, "selfsigned-root.pfx");

    /// <summary>Gets the file listing DNS-01 TXT records still to be removed.</summary>
    public string AcmeTxtCleanupFile => Path.Join(AcmeDirectory, "txt-cleanup.json");

    /// <summary>
    /// Gets the recovery flag. When this file exists at startup, Jellyfin's network settings are restored and
    /// the proxy stays off. Creating it is the documented way out of a broken setup without the dashboard.
    /// </summary>
    public string DisableFlag => Path.Join(DataDirectory, "DISABLE");
}
