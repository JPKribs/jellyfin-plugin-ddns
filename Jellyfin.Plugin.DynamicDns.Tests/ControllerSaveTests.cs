using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.DynamicDns.Api;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the dashboard save endpoint: an edit that retargets a record starts its runtime status over
/// while a cosmetic edit keeps it, and a stored credential can be explicitly cleared while a blank or
/// sentinel field keeps it. Also pins that Run now is not tied to the request's abort token.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class ControllerSaveTests
{
    private const string Kept = "__JPK_DDNS_SECRET_KEPT__";
    private const string Clear = "__JPK_DDNS_SECRET_CLEAR__";

    private static DynamicDNSController Controller(OrchestratorHarness h)
        => new(h.Service, h.Status, h.Secrets, h.ActivityLog, h.Providers);

    private static RecordStatus PausedStatus() => new()
    {
        LastIPv4 = "198.51.100.1",
        LastSuccess = false,
        LastCheckedUtc = DateTime.UtcNow,
        ConsecutiveFailures = 3,
        BackoffUntilUtc = DateTime.UtcNow + TimeSpan.FromHours(20)
    };

    // What the dashboard posts back: the redacted record (credentials as the sentinel) with edits applied.
    private static PluginConfiguration Posted(DynamicDNSController controller, Action<DNSRecord> edit)
    {
        var current = (PluginConfiguration)((OkObjectResult)controller.GetConfiguration().Result!).Value!;
        edit(current.Records[0]);
        return current;
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("provider")]
    [InlineData("zone")]
    [InlineData("server")]
    [InlineData("password")]
    public void RetargetingEdit_ClearsTheRecordsStatus(string field)
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord(), OrchestratorHarness.DynDnsRecord("other", "other.example.com"));
        h.SeedStatus("rec1", PausedStatus());
        h.SeedStatus("other", PausedStatus());
        var controller = Controller(h);

        controller.SaveConfiguration(Posted(controller, r =>
        {
            switch (field)
            {
                case "hostname": r.Hostname = "new.example.com"; break;
                case "provider": r.Provider = DNSProviderKind.NoIp; break;
                case "zone": r.Zone = "example.com"; break;
                case "server": r.Server = "https://other.test"; break;
                default: r.Password = "a-new-password"; break;
            }
        }));

        var records = h.Status.Load().Records;
        Assert.False(records.ContainsKey("rec1"));

        // The untouched record keeps its status.
        Assert.Equal(3, records["other"].ConsecutiveFailures);
    }

    [Fact]
    public void CosmeticEdit_KeepsTheRecordsStatus()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", PausedStatus());
        var controller = Controller(h);

        controller.SaveConfiguration(Posted(controller, r =>
        {
            r.Name = "renamed";
            r.Enabled = false;
        }));

        Assert.Equal(3, h.StatusOf("rec1").ConsecutiveFailures);
    }

    [Fact]
    public void ClearSentinel_RemovesTheStoredCredential_BlankAndKeptKeepIt()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        var controller = Controller(h);

        controller.SaveConfiguration(Posted(controller, r =>
        {
            r.Login = string.Empty;
            r.Password = Clear;
        }));

        var stored = h.Plugin.ReadConfiguration(c => c.Records[0].Clone());
        Assert.Equal("user", stored.Login);
        Assert.Equal(string.Empty, stored.Password);

        controller.SaveConfiguration(Posted(controller, r => r.Login = Kept));
        Assert.Equal("user", h.Plugin.ReadConfiguration(c => c.Records[0].Login));

        controller.SaveConfiguration(Posted(controller, r => r.Login = Clear));
        Assert.Equal(string.Empty, h.Plugin.ReadConfiguration(c => c.Records[0].Login));
    }

    [Fact]
    public void ClearingACredential_AlsoResetsStatus()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", PausedStatus());
        var controller = Controller(h);

        controller.SaveConfiguration(Posted(controller, r => r.Password = Clear));

        Assert.False(h.Status.Load().Records.ContainsKey("rec1"));
    }

    [Fact]
    public void RetargetedIds_OnlyReportsKeptRecordsWhoseTargetChanged()
    {
        var before = new List<DNSRecord>
        {
            new() { Id = "a", Hostname = "a.example.com" },
            new() { Id = "b", Hostname = "b.example.com" },
            new() { Id = "gone", Hostname = "g.example.com" }
        };
        var after = new List<DNSRecord>
        {
            new() { Id = "a", Hostname = "a.example.com", Name = "renamed" },
            new() { Id = "b", Hostname = "moved.example.com" },
            new() { Id = "new", Hostname = "n.example.com" }
        };

        Assert.Equal(new[] { "b" }, DynamicDNSController.RetargetedIds(before, after).ToArray());
        Assert.Empty(DynamicDNSController.RetargetedIds(null, after));
    }

    [Fact]
    public void RunNow_DoesNotTakeTheRequestAbortToken()
    {
        var method = typeof(DynamicDNSController).GetMethod(nameof(DynamicDNSController.RunNow), BindingFlags.Public | BindingFlags.Instance)!;
        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(System.Threading.CancellationToken));
    }
}
