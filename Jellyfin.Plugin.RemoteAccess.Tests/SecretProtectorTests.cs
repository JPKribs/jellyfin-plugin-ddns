using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Tests for credential encryption-at-rest. Uses an in-memory data protector so no key ring touches disk.
/// </summary>
public class SecretProtectorTests
{
    private static SecretProtector Protector()
        => new("Test.Secrets.v1", NullLogger.Instance, new EphemeralDataProtectionProvider());

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var p = Protector();
        var blob = p.Protect("super-secret-token");

        Assert.True(SecretProtector.IsProtected(blob));
        Assert.NotEqual("super-secret-token", blob);
        Assert.Equal("super-secret-token", p.Unprotect(blob));
    }

    [Fact]
    public void Protect_IsIdempotent_DoesNotDoubleEncrypt()
    {
        var p = Protector();
        var once = p.Protect("k");
        var twice = p.Protect(once);

        Assert.Equal(once, twice);
        Assert.Equal("k", p.Unprotect(twice));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Protect_EmptyOrNull_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, Protector().Protect(input));

    [Fact]
    public void Unprotect_PlaintextValue_ReturnedUnchanged_ForMigration()
    {
        // A pre-migration value has no prefix and must be read back as-is so existing configs keep working.
        Assert.Equal("legacy-plaintext", Protector().Unprotect("legacy-plaintext"));
    }

    [Fact]
    public void WithoutDataProtection_DegradesToPlaintext_NoThrow()
    {
        var p = new SecretProtector("Test.Secrets.v1", NullLogger.Instance);

        Assert.Equal("k", p.Protect("k"));             // no-op, not encrypted
        Assert.False(SecretProtector.IsProtected(p.Protect("k")));
        Assert.Equal("k", p.Unprotect("k"));           // plaintext still readable
    }

    [Fact]
    public void WithSecrets_DoesNotMutateOriginalRecord()
    {
        var original = new DNSRecord { Login = "enc:v1:blob", Password = "enc:v1:blob" };
        var live = original.WithSecrets("user", "pass");

        Assert.Equal("user", live.Login);
        Assert.Equal("pass", live.Password);
        Assert.Equal("enc:v1:blob", original.Login);   // original untouched
        Assert.Equal("enc:v1:blob", original.Password);
    }
}
