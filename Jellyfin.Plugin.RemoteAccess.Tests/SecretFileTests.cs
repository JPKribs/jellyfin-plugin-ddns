using System;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Key and certificate files are replaced whole and readable by the owner only.
/// </summary>
public sealed class SecretFileTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "ddns-secret-tests-" + Guid.NewGuid().ToString("N"));

    public SecretFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_ReplacesTheFileAndLeavesNoTempBehind()
    {
        var path = Path.Join(_dir, "key.pem");
        SecretFile.Write(path, "first"u8);
        SecretFile.Write(path, "second"u8);

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Join(_dir, "key.pem");
        SecretFile.Write(path, "secret"u8);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
