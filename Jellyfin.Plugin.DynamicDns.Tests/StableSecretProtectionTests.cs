using System;
using System.IO;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Proves the shared protector reads the secrets the plugin stored before it moved onto the base package:
/// the key folder, Data Protection application name, and purpose are unchanged, so a secret encrypted under
/// the old hand-built values still decrypts.
/// </summary>
public class StableSecretProtectionTests
{
    private static string TempDir() =>
        Path.Join(Path.GetTempPath(), "ddns-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecretsName_DerivesThePreviousKeyFolderAppNameAndPurpose()
    {
        var dir = TempDir();
        try
        {
            var paths = Substitute.For<IApplicationPaths>();
            paths.PluginConfigurationsPath.Returns(dir);

            Assert.Equal("Jellyfin.Plugin.DynamicDns", Plugin.SecretsName);
            Assert.Equal(
                Path.Join(dir, "Jellyfin.Plugin.DynamicDns.Keys"),
                StableSecretProtection.KeyDirectory(paths, Plugin.SecretsName));

            // Encrypt the way the plugin did before: keys in <configs>/Jellyfin.Plugin.DynamicDns.Keys, the
            // application name Jellyfin.Plugin.DynamicDns, and the purpose Jellyfin.Plugin.DynamicDns.Secrets.v1.
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Join(dir, "Jellyfin.Plugin.DynamicDns.Keys")))
                .SetApplicationName("Jellyfin.Plugin.DynamicDns");
            string stored;
            using (var container = services.BuildServiceProvider())
            {
                var legacy = new SecretProtector(
                    "Jellyfin.Plugin.DynamicDns.Secrets.v1",
                    NullLogger.Instance,
                    container.GetRequiredService<IDataProtectionProvider>());
                stored = legacy.Protect("super-secret-password");
            }

            Assert.True(SecretProtector.IsProtected(stored));

            // A fresh protector built by the shared helper from the same configurations folder reads it.
            var current = StableSecretProtection.CreateProtector(paths, Plugin.SecretsName, NullLogger.Instance);
            Assert.True(current.IsAvailable);
            Assert.Equal("super-secret-password", current.Unprotect(stored));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Build_RoundTripsWithKeysInTheGivenDirectory()
    {
        var dir = TempDir();
        try
        {
            var provider = StableSecretProtection.Build(dir, Plugin.SecretsName, NullLogger.Instance);
            Assert.NotNull(provider);

            var protector = provider!.CreateProtector("Jellyfin.Plugin.DynamicDns.Secrets.v1");
            var encrypted = protector.Protect("api-token");

            Assert.NotEqual("api-token", encrypted);
            Assert.Equal("api-token", protector.Unprotect(encrypted));
            Assert.NotEmpty(Directory.GetFiles(dir, "key-*.xml"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void CreateProtector_DegradesToPlaintext_WhenKeyDirectoryUnusable()
    {
        // A file where the configurations folder should be makes the key folder impossible to create.
        var file = Path.GetTempFileName();
        try
        {
            var paths = Substitute.For<IApplicationPaths>();
            paths.PluginConfigurationsPath.Returns(file);

            var protector = StableSecretProtection.CreateProtector(paths, Plugin.SecretsName, NullLogger.Instance);

            Assert.False(protector.IsAvailable);
            Assert.Equal("hunter2", protector.Protect("hunter2"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
