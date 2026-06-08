using System;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Proves the plugin-managed Data Protection key is durable: a secret encrypted
/// by one provider instance still decrypts after the process is "restarted"
/// (a brand-new provider built from the same on-disk key directory and the same
/// application name), which is the scenario that broke under the host provider.
/// </summary>
public class StableSecretProtectionTests
{
    private static string TempKeyDir() =>
        Path.Combine(Path.GetTempPath(), "ddns-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Build_RoundTripsWithKeysInTheGivenDirectory()
    {
        var dir = TempKeyDir();
        try
        {
            var provider = StableSecretProtection.Build(dir, NullLogger.Instance);
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
    public void Build_DecryptsAcrossProcessRestart()
    {
        var dir = TempKeyDir();
        try
        {
            // First "process": encrypt a secret via the plugin SecretProtector.
            var first = new SecretProtector(
                "Jellyfin.Plugin.DynamicDns.Secrets.v1",
                NullLogger.Instance,
                StableSecretProtection.Build(dir, NullLogger.Instance));
            var stored = first.Protect("super-secret-password");
            Assert.True(SecretProtector.IsProtected(stored));

            // Second "process": a brand-new provider + protector built from the
            // SAME key directory and application name must read the stored value.
            var second = new SecretProtector(
                "Jellyfin.Plugin.DynamicDns.Secrets.v1",
                NullLogger.Instance,
                StableSecretProtection.Build(dir, NullLogger.Instance));

            Assert.Equal("super-secret-password", second.Unprotect(stored));
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
    public void Build_ReturnsNull_WhenKeyDirectoryUnusable()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Null(StableSecretProtection.Build(Path.Combine(file, "keys"), NullLogger.Instance));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
