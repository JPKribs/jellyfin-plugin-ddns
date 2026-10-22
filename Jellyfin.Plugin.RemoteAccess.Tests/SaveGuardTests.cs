using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Api;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// What the save endpoints and the block list refuse or tidy up before anything is stored.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class SaveGuardTests
{
    [Fact]
    public void DashboardSave_ARecordWithoutAHostname_IsRefused()
    {
        using var h = new OrchestratorHarness();
        var controller = new DynamicDNSController(h.Service, h.Status, h.Secrets, h.ActivityLog, h.Providers);
        var posted = new PluginConfiguration { Records = new List<DNSRecord> { new() { Provider = DNSProviderKind.DynDns2, Hostname = " " } } };
        Assert.IsType<BadRequestObjectResult>(controller.SaveConfiguration(posted).Result);
    }

    [Fact]
    public void GenericSave_GivesMissingAndRepeatedIdsTheirOwn()
    {
        using var h = new OrchestratorHarness();
        var config = new PluginConfiguration
        {
            Records = new List<DNSRecord>
            {
                new() { Id = string.Empty, Provider = DNSProviderKind.DynDns2, Hostname = "a.example.com" },
                new() { Id = "same", Provider = DNSProviderKind.DynDns2, Hostname = "b.example.com" },
                new() { Id = "same", Provider = DNSProviderKind.DynDns2, Hostname = "c.example.com" }
            }
        };
        h.Plugin.UpdateConfiguration(config);
        var ids = h.Plugin.Configuration.Records.Select(r => r.Id).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void BlockList_AnIPv4MappedEntry_NamesTheIPv4Address()
    {
        Assert.True(BlockGuard.TryParseTarget("203.0.113.5", out var target));
        Assert.True(BlockGuard.NamesOnly("::ffff:203.0.113.5", target));
        Assert.True(BlockGuard.NamesOnly("203.0.113.5/32", target));
        Assert.False(BlockGuard.NamesOnly("203.0.113.0/24", target));
    }

    [Fact]
    public void Validator_NullListEntries_AreDropped()
    {
        var s = new ProxySettings();
        s.Advanced.TrustedNetworks = new List<string> { null!, "10.0.0.0/8" };
        s.Advanced.BlockedAddresses = new List<string> { null!, "203.0.113.5" };
        ProxySettingsValidator.Normalize(s);
        Assert.Equal(new[] { "10.0.0.0/8" }, s.Advanced.TrustedNetworks);
        Assert.Equal(new[] { "203.0.113.5" }, s.Advanced.BlockedAddresses);
    }
}
