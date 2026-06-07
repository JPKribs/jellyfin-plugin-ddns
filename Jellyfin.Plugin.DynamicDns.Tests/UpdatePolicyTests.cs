using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Verifies the "don't spam the provider" rule: skip only when the address is unchanged AND the
/// previous update succeeded; otherwise update (changed address or prior failure).
/// </summary>
public class UpdatePolicyTests
{
    private static DnsRecord SucceededAt(string v4) => new()
    {
        UpdateIPv4 = true,
        LastIPv4 = v4,
        LastSuccess = true
    };

    [Fact]
    public void Unchanged_AfterSuccess_Skips()
    {
        var record = SucceededAt("1.2.3.4");
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv4 = "1.2.3.4" });
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void Changed_AfterSuccess_Updates()
    {
        var record = SucceededAt("1.2.3.4");
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv4 = "5.6.7.8" });
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void Unchanged_AfterFailure_Retries()
    {
        // Same IP, but the previous attempt failed — must retry rather than skip.
        var record = new DnsRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = false };
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv4 = "1.2.3.4" });
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void NoUsableAddress_SkipsNoAddress()
    {
        // IPv4 wanted but none detected; IPv6 not enabled.
        var record = new DnsRecord { UpdateIPv4 = true, UpdateIPv6 = false, LastSuccess = true };
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv4 = null, IPv6 = "2001:db8::1" });
        Assert.Equal(UpdateDecision.SkipNoAddress, decision);
    }

    [Fact]
    public void Ipv6Changed_AfterSuccess_Updates()
    {
        var record = new DnsRecord
        {
            UpdateIPv4 = false,
            UpdateIPv6 = true,
            LastIPv6 = "2001:db8::1",
            LastSuccess = true
        };
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv6 = "2001:db8::2" });
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void FirstRun_NeverUpdatedYet_Updates()
    {
        // LastSuccess defaults to false and no LastIPv4 stored: treat as needing an update.
        var record = new DnsRecord { UpdateIPv4 = true };
        var decision = UpdatePolicy.Decide(record, new DetectedIp { IPv4 = "1.2.3.4" });
        Assert.Equal(UpdateDecision.Update, decision);
    }
}
