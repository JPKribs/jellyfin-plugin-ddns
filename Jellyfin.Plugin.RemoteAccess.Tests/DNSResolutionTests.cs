using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the address matching in <see cref="DNSResolution"/> and the guards in
/// <see cref="DNSLookupService"/> that do not need a real resolver.
/// </summary>
public class DNSResolutionTests
{
    [Fact]
    public void Serves_ExactV4_True()
    {
        var dns = DNSResolution.Resolved(new[] { IPAddress.Parse("1.2.3.4") });
        Assert.True(dns.Serves("1.2.3.4"));
        Assert.False(dns.Serves("1.2.3.5"));
    }

    [Fact]
    public void Serves_V6_NormalizesFormatting()
    {
        // A compressed and an expanded form of the same address must match.
        var dns = DNSResolution.Resolved(new[] { IPAddress.Parse("2001:db8::1") });
        Assert.True(dns.Serves("2001:0db8:0000:0000:0000:0000:0000:0001"));
    }

    [Fact]
    public void Serves_WrongFamilyOrNull_False()
    {
        var dns = DNSResolution.Resolved(new[] { IPAddress.Parse("1.2.3.4") });
        Assert.False(dns.Serves("2001:db8::1"));
        Assert.False(dns.Serves(null));
        Assert.False(dns.Serves("not-an-ip"));
    }

    [Fact]
    public void Failed_ServesNothing()
    {
        Assert.False(DNSResolution.Failed.Serves("1.2.3.4"));
    }

    [Fact]
    public void ServesPublic_TrueOnlyForAPublicAddressOfTheFamily()
    {
        Assert.True(DNSResolution.Resolved(new[] { IPAddress.Parse("1.2.3.4") }).ServesPublic(AddressFamily.InterNetwork));
        // A private answer is a split horizon resolver, so it does not count as serving the public record.
        Assert.False(DNSResolution.Resolved(new[] { IPAddress.Parse("10.100.0.2") }).ServesPublic(AddressFamily.InterNetwork));
        // Right family only: a public v4 does not satisfy a v6 question.
        Assert.False(DNSResolution.Resolved(new[] { IPAddress.Parse("1.2.3.4") }).ServesPublic(AddressFamily.InterNetworkV6));
        Assert.False(DNSResolution.Failed.ServesPublic(AddressFamily.InterNetwork));
    }

    [Fact]
    public async Task Lookup_BlankHostname_ReturnsFailed()
    {
        var svc = new DNSLookupService(NullLogger<DNSLookupService>.Instance);
        var result = await svc.ResolveAsync("   ", CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("1.2.3.4")]
    [InlineData("2001:db8::1")]
    public async Task Lookup_IpLiteral_ReturnsFailed_WithoutResolving(string literal)
    {
        // A numeric record id parses as an IPv4 literal, and the resolver would echo it back as if DNS
        // served that address.
        var resolved = false;
        var svc = new DNSLookupService(NullLogger<DNSLookupService>.Instance, (_, _) =>
        {
            resolved = true;
            return Task.FromResult(new[] { IPAddress.Parse("0.1.226.64") });
        });

        var result = await svc.ResolveAsync(literal, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(resolved);
    }
}
