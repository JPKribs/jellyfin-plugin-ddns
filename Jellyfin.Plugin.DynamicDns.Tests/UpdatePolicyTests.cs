using System;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Verifies the update decision. The detected IP is compared against what the hostname currently resolves
/// to in DNS, so a record is skipped only when live DNS already serves it, and a record changed elsewhere
/// is re-pushed. An unchanged record is forced when a force interval is set and the last push is older.
/// </summary>
public class UpdatePolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

    private static DNSResolution Serving(params string[] ips)
        => DNSResolution.Resolved(ips.Select(IPAddress.Parse).ToList());

    private static UpdateDecision Decide(DNSRecord record, DetectedIP ip, DNSResolution dns)
        => UpdatePolicy.Decide(record, ip, dns, TimeSpan.Zero, Now);

    [Fact]
    public void DnsServesDetected_Skips()
    {
        var record = new DNSRecord { UpdateIPv4 = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("1.2.3.4"));
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void DnsServesDifferentIp_Updates()
    {
        // DNS still serves the old address while detection sees a new one, so the record is pushed.
        var record = new DNSRecord { UpdateIPv4 = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, Serving("1.2.3.4"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void DnsChangedExternally_Updates()
    {
        // Something else pointed the record at an unrelated address. We re-push the detected IP.
        var record = new DNSRecord { UpdateIPv4 = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("9.9.9.9"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void DNSLookupFailed_Updates()
    {
        // A missing record or a resolver error serves nothing, so push rather than skip.
        var record = new DNSRecord { UpdateIPv4 = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, DNSResolution.Failed);
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void SplitHorizon_PrivateDNSAnswer_UnchangedIp_Skips()
    {
        // A local resolver answers the hostname with an internal address, so DNS never matches the public
        // IP. With the detected IP equal to the last pushed one, the record must not be re-pushed.
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("10.100.0.2"));
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void SplitHorizon_PrivateDNSAnswer_FirstRun_Updates()
    {
        // Same split horizon, but nothing has been pushed yet, so the record is established.
        var record = new DNSRecord { UpdateIPv4 = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("10.100.0.2"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void SplitHorizon_PrivateDNSAnswer_DetectedIpChanged_Updates()
    {
        // The public IP actually changed, so even with a split horizon answer the record is pushed.
        var record = new DNSRecord { UpdateIPv4 = true, LastIPv4 = "1.2.3.4", LastSuccess = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, Serving("10.100.0.2"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void NoUsableAddress_SkipsNoAddress()
    {
        // IPv4 wanted but none detected, and IPv6 is not enabled.
        var record = new DNSRecord { UpdateIPv4 = true, UpdateIPv6 = false };
        var decision = Decide(record, new DetectedIP { IPv4 = null, IPv6 = "2001:db8::1" }, DNSResolution.Failed);
        Assert.Equal(UpdateDecision.SkipNoAddress, decision);
    }

    [Fact]
    public void Ipv6_DnsServesDetected_Skips()
    {
        var record = new DNSRecord { UpdateIPv4 = false, UpdateIPv6 = true };
        var decision = Decide(record, new DetectedIP { IPv6 = "2001:db8::1" }, Serving("2001:db8::1"));
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void Ipv6_DnsServesDifferent_Updates()
    {
        var record = new DNSRecord { UpdateIPv4 = false, UpdateIPv6 = true };
        var decision = Decide(record, new DetectedIP { IPv6 = "2001:db8::2" }, Serving("2001:db8::1"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void Proxied_DnsHidesOrigin_ComparesLastPushed()
    {
        // DNS serves the Cloudflare proxy address, not the origin. A proxied record must not re-push every
        // run just because DNS does not match. It skips when the detected IP equals the last pushed one.
        var record = new DNSRecord { UpdateIPv4 = true, Proxied = true, LastIPv4 = "1.2.3.4", LastSuccess = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("104.16.0.1"));
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void Proxied_DetectedIpChanged_Updates()
    {
        var record = new DNSRecord { UpdateIPv4 = true, Proxied = true, LastIPv4 = "1.2.3.4", LastSuccess = true };
        var decision = Decide(record, new DetectedIP { IPv4 = "5.6.7.8" }, Serving("104.16.0.1"));
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void Unchanged_WithinForceWindow_Skips()
    {
        // Force interval is 24 hours and the last push was only an hour ago, so it still skips.
        var record = new DNSRecord { UpdateIPv4 = true, LastUpdateUtc = Now.AddHours(-1) };
        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("1.2.3.4"), TimeSpan.FromHours(24), Now);
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }

    [Fact]
    public void Unchanged_BeyondForceWindow_Updates()
    {
        // DNS already serves the detected IP, but the last push is older than the force interval.
        var record = new DNSRecord { UpdateIPv4 = true, LastUpdateUtc = Now.AddHours(-25) };
        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("1.2.3.4"), TimeSpan.FromHours(24), Now);
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void Unchanged_ForceEnabled_NoPushTimeYet_Updates()
    {
        var record = new DNSRecord { UpdateIPv4 = true, LastUpdateUtc = null };
        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("1.2.3.4"), TimeSpan.FromHours(24), Now);
        Assert.Equal(UpdateDecision.Update, decision);
    }

    [Fact]
    public void Unchanged_ForceDisabled_Skips()
    {
        // With forcing off, even a very old push stays skipped while DNS already serves the IP.
        var record = new DNSRecord { UpdateIPv4 = true, LastUpdateUtc = Now.AddDays(-30) };
        var decision = UpdatePolicy.Decide(record, new DetectedIP { IPv4 = "1.2.3.4" }, Serving("1.2.3.4"), TimeSpan.Zero, Now);
        Assert.Equal(UpdateDecision.SkipUnchanged, decision);
    }
}
