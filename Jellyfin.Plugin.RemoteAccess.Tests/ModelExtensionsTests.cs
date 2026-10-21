using System;
using Jellyfin.Plugin.DynamicDns.Models;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the model extensions that hold derived values on the model rather than in the services:
/// the record display name and want-family checks, and the update decision presentation.
/// </summary>
public class ModelExtensionsTests
{
    [Fact]
    public void DisplayName_PrefersName_FallsBackToHostname()
    {
        Assert.Equal("My Record", new DNSRecord { Name = "My Record", Hostname = "home.example.com" }.DisplayName());
        Assert.Equal("home.example.com", new DNSRecord { Name = "", Hostname = "home.example.com" }.DisplayName());
        Assert.Equal("home.example.com", new DNSRecord { Name = "   ", Hostname = "home.example.com" }.DisplayName());
    }

    [Theory]
    [InlineData(true, "1.2.3.4", true)]
    [InlineData(false, "1.2.3.4", false)]   // record does not update A records
    [InlineData(true, null, false)]         // no IPv4 detected
    public void WantsIPv4_CombinesFlagAndDetection(bool updateV4, string? detected, bool expected)
    {
        var record = new DNSRecord { UpdateIPv4 = updateV4 };
        Assert.Equal(expected, record.WantsIPv4(new DetectedIP { IPv4 = detected }));
    }

    [Theory]
    [InlineData(true, "2001:db8::1", true)]
    [InlineData(false, "2001:db8::1", false)]
    [InlineData(true, null, false)]
    public void WantsIPv6_CombinesFlagAndDetection(bool updateV6, string? detected, bool expected)
    {
        var record = new DNSRecord { UpdateIPv6 = updateV6 };
        Assert.Equal(expected, record.WantsIPv6(new DetectedIP { IPv6 = detected }));
    }

    [Theory]
    [InlineData(UpdateDecision.Update, false)]
    [InlineData(UpdateDecision.SkipNoAddress, true)]
    [InlineData(UpdateDecision.SkipUnchanged, true)]
    public void IsSkip_IsTrueForEverythingButUpdate(UpdateDecision decision, bool expected)
        => Assert.Equal(expected, decision.IsSkip());

    [Fact]
    public void SkipOutcome_MapsEachSkip()
    {
        var noAddress = UpdateDecision.SkipNoAddress.SkipOutcome();
        Assert.Equal("No address", noAddress.Action);
        Assert.False(noAddress.Succeeded);

        var unchanged = UpdateDecision.SkipUnchanged.SkipOutcome();
        Assert.Equal("IP Unchanged", unchanged.Action);
        Assert.True(unchanged.Succeeded);
    }

    [Fact]
    public void SkipOutcome_ThrowsForUpdate()
        => Assert.Throws<ArgumentOutOfRangeException>(() => UpdateDecision.Update.SkipOutcome());
}
