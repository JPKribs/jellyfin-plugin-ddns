using System;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Tests for the shared credential encryption pass that guards every configuration write path,
/// including the UpdateConfiguration hook on the generic plugin configuration endpoint.
/// </summary>
public class CredentialEncryptionTests
{
    private static string TempKeyDir() =>
        Path.Join(Path.GetTempPath(), "ddns-keys-" + Guid.NewGuid().ToString("N"));

    private static SecretProtector Protector(string dir) => new(
        "Jellyfin.Plugin.DynamicDns.Secrets.v1",
        NullLogger.Instance,
        StableSecretProtection.Build(dir, NullLogger.Instance));

    [Fact]
    public void ProtectAll_EncryptsPlaintextCredentials()
    {
        var dir = TempKeyDir();
        try
        {
            var secrets = Protector(dir);
            var record = new DNSRecord { Login = "user", Password = "hunter2" };

            var changed = CredentialEncryption.ProtectAll(new[] { record }, secrets);

            Assert.True(changed);
            Assert.True(SecretProtector.IsProtected(record.Login));
            Assert.True(SecretProtector.IsProtected(record.Password));
            Assert.Equal("user", secrets.Unprotect(record.Login));
            Assert.Equal("hunter2", secrets.Unprotect(record.Password));
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
    public void ProtectAll_LeavesEncryptedAndEmptyValuesUntouched()
    {
        var dir = TempKeyDir();
        try
        {
            var secrets = Protector(dir);
            var record = new DNSRecord { Login = secrets.Protect("user"), Password = string.Empty };
            var storedLogin = record.Login;

            var changed = CredentialEncryption.ProtectAll(new[] { record }, secrets);

            Assert.False(changed);
            Assert.Equal(storedLogin, record.Login);
            Assert.Equal(string.Empty, record.Password);
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
    public void ProtectAll_WithoutDataProtection_ReportsNoChange()
    {
        // A degraded protector passes values through, so the pass must not claim a change happened.
        var secrets = new SecretProtector("Jellyfin.Plugin.DynamicDns.Secrets.v1", NullLogger.Instance);
        var record = new DNSRecord { Login = "user", Password = "hunter2" };

        var changed = CredentialEncryption.ProtectAll(new[] { record }, secrets);

        Assert.False(changed);
        Assert.Equal("user", record.Login);
        Assert.Equal("hunter2", record.Password);
    }

    [Fact]
    public void Clone_CopiesFieldsToADistinctInstance()
    {
        var record = new DNSRecord { Id = "abc", Hostname = "home.example.com", Login = "user" };

        var copy = record.Clone();
        copy.Login = "other";

        Assert.NotSame(record, copy);
        Assert.Equal("abc", copy.Id);
        Assert.Equal("home.example.com", copy.Hostname);
        Assert.Equal("user", record.Login);
    }
}
