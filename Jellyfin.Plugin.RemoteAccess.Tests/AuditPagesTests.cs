using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Api;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the save paths behind the DDNS records, the Dynamic DNS section of Advanced and the generic configuration endpoint: a
/// provider that hides the hostname field can be saved, client-sent runtime status is ignored, intervals
/// are clamped, the generic endpoint understands the dashboard's credential placeholders and resets a
/// retargeted record's status, and stored provider replies are always XML safe.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class AuditPagesTests
{
    private const string Kept = "__JPK_DDNS_SECRET_KEPT__";
    private const string Clear = "__JPK_DDNS_SECRET_CLEAR__";

    private static OrchestratorHarness WithInwx()
        => new(http => new IDNSProvider[] { new InwxProvider(http, NullLogger<InwxProvider>.Instance) });

    private static DynamicDNSController Controller(OrchestratorHarness h)
        => new(h.Service, h.Status, h.Secrets, h.ActivityLog, h.Providers);

    private static DNSRecord InwxRecord(string id = "inwx") => new()
    {
        Id = id,
        Name = id,
        Provider = DNSProviderKind.Inwx,
        Hostname = string.Empty,
        Login = "user",
        Password = "pass",
        UpdateIPv4 = true
    };

    [Fact]
    public void DashboardSave_HostnameHiddenByProvider_IsSaved()
    {
        using var h = WithInwx();
        var posted = new PluginConfiguration { Records = new List<DNSRecord> { InwxRecord() } };
        posted.Records[0].Hostname = null!;

        Assert.IsType<OkObjectResult>(Controller(h).SaveConfiguration(posted).Result);
        var stored = h.Plugin.ReadConfiguration(c => c.Records.Single().Clone());
        Assert.Equal(DNSProviderKind.Inwx, stored.Provider);
        Assert.Equal(string.Empty, stored.Hostname);
    }

    [Fact]
    public void DashboardSave_ProviderThatShowsHostname_StillNeedsOne()
    {
        using var h = WithInwx();
        var record = OrchestratorHarness.DynDnsRecord();
        record.Hostname = "  ";
        var posted = new PluginConfiguration { Records = new List<DNSRecord> { InwxRecord(), record } };

        Assert.IsType<BadRequestObjectResult>(Controller(h).SaveConfiguration(posted).Result);
    }

    [Theory]
    [InlineData(DNSProviderKind.Inwx, false)]
    [InlineData(DNSProviderKind.CloudNs, false)]
    [InlineData(DNSProviderKind.Directnic, false)]
    [InlineData(DNSProviderKind.DynDns2, true)]
    [InlineData(DNSProviderKind.Cloudflare, true)]
    public void GenericSave_HostnameIsRequiredOnlyWhereTheProviderShowsIt(DNSProviderKind kind, bool required)
    {
        using var h = new OrchestratorHarness();
        var config = new PluginConfiguration
        {
            Records = new List<DNSRecord> { new() { Id = "r", Provider = kind, Hostname = string.Empty } }
        };

        if (required)
        {
            Assert.Throws<ArgumentException>(() => h.Plugin.UpdateConfiguration(config));
        }
        else
        {
            h.Plugin.UpdateConfiguration(config);
            Assert.Equal(kind, h.Plugin.Configuration.Records.Single().Provider);
        }
    }

    [Fact]
    public void DashboardSave_ClientSentStatus_IsIgnoredAndCannotBlockTheSave()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        var controller = Controller(h);
        var posted = (PluginConfiguration)((OkObjectResult)controller.GetConfiguration().Result!).Value!;
        posted.LastDetectionMessage = "bad \u0001 reply";
        posted.Records[0].LastStatus = "server said: ￾";
        posted.Records[0].LastAction = "Updated";
        posted.Records[0].ConsecutiveFailures = 99;

        var result = controller.SaveConfiguration(posted).Result;

        var saved = (PluginConfiguration)Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(string.Empty, saved.Records[0].LastStatus);
        Assert.Equal(0, saved.Records[0].ConsecutiveFailures);
        Assert.Equal(string.Empty, h.Plugin.ReadConfiguration(c => c.Records[0].LastStatus));
        Assert.False(h.Status.Load().Records.ContainsKey("rec1"));
    }

    [Fact]
    public void GenericSave_ClientSentStatus_IsIgnored()
    {
        using var h = new OrchestratorHarness();
        var record = OrchestratorHarness.DynDnsRecord();
        record.LastStatus = "server said: ￿";
        var config = new PluginConfiguration { Records = new List<DNSRecord> { record }, LastDetectionMessage = "\u0002" };

        h.Plugin.UpdateConfiguration(config);

        Assert.Equal(string.Empty, h.Plugin.Configuration.Records[0].LastStatus);
        Assert.Equal(string.Empty, h.Plugin.Configuration.LastDetectionMessage);
    }

    [Fact]
    public void BothSaves_ClampHugeIntervals()
    {
        using var h = new OrchestratorHarness();
        var posted = new PluginConfiguration { ForceUpdateHours = int.MaxValue, BackoffHours = int.MaxValue, RequestTimeoutSeconds = int.MaxValue };
        Assert.IsType<OkObjectResult>(Controller(h).SaveConfiguration(posted).Result);
        Assert.Equal(DynamicDNSController.MaxIntervalHours, h.Plugin.Configuration.ForceUpdateHours);
        Assert.Equal(DynamicDNSController.MaxIntervalHours, h.Plugin.Configuration.BackoffHours);
        Assert.Equal(300, h.Plugin.Configuration.RequestTimeoutSeconds);

        h.Plugin.UpdateConfiguration(new PluginConfiguration { ForceUpdateHours = int.MaxValue, BackoffHours = int.MinValue });
        Assert.Equal(DynamicDNSController.MaxIntervalHours, h.Plugin.Configuration.ForceUpdateHours);
        Assert.Equal(1, h.Plugin.Configuration.BackoffHours);

        // The clamped values are ones the update run can turn into a time span and add to a date.
        _ = DateTime.UtcNow + TimeSpan.FromHours(h.Plugin.Configuration.ForceUpdateHours);
    }

    [Fact]
    public void GenericSave_KeptPlaceholderKeepsTheStoredValue_ClearPlaceholderEmptiesIt()
    {
        using var h = new OrchestratorHarness();
        var stored = OrchestratorHarness.DynDnsRecord();
        stored.Login = h.Plugin.Secrets.Protect("user");
        stored.Password = h.Plugin.Secrets.Protect("pass");
        h.SetRecords(stored);
        var storedLogin = stored.Login;

        var posted = stored.Clone();
        posted.Login = Kept;
        posted.Password = Clear;
        h.Plugin.UpdateConfiguration(new PluginConfiguration { Records = new List<DNSRecord> { posted } });

        var saved = h.Plugin.Configuration.Records.Single();
        Assert.Equal(storedLogin, saved.Login);
        Assert.Equal("user", h.Plugin.Secrets.Unprotect(saved.Login));
        Assert.Equal(string.Empty, saved.Password);
    }

    [Fact]
    public void GenericSave_KeptPlaceholderOnAMovedRecord_DoesNotFollowIt()
    {
        using var h = new OrchestratorHarness();
        var stored = OrchestratorHarness.DynDnsRecord();
        stored.Password = h.Plugin.Secrets.Protect("pass");
        h.SetRecords(stored);

        var posted = stored.Clone();
        posted.Server = "https://elsewhere.test";
        posted.Password = Kept;
        h.Plugin.UpdateConfiguration(new PluginConfiguration { Records = new List<DNSRecord> { posted } });

        Assert.Equal(string.Empty, h.Plugin.Configuration.Records.Single().Password);
    }

    [Fact]
    public void GenericSave_RetargetedRecord_StartsItsStatusOver()
    {
        using var h = new OrchestratorHarness();
        var first = OrchestratorHarness.DynDnsRecord();
        first.Password = h.Plugin.Secrets.Protect("pass");
        first.Login = h.Plugin.Secrets.Protect("user");
        var second = OrchestratorHarness.DynDnsRecord("other", "other.example.com");
        second.Password = first.Password;
        second.Login = first.Login;
        h.SetRecords(first, second);
        h.SeedStatus("rec1", new RecordStatus { ConsecutiveFailures = 3, LastCheckedUtc = DateTime.UtcNow });
        h.SeedStatus("other", new RecordStatus { ConsecutiveFailures = 3, LastCheckedUtc = DateTime.UtcNow });

        var moved = first.Clone();
        moved.Hostname = "moved.example.com";
        var renamed = second.Clone();
        renamed.Name = "renamed";
        h.Plugin.UpdateConfiguration(new PluginConfiguration { Records = new List<DNSRecord> { moved, renamed } });

        var status = h.Status.Load().Records;
        Assert.False(status.ContainsKey("rec1"));
        Assert.Equal(3, status["other"].ConsecutiveFailures);
    }

    [Fact]
    public void GenericSave_RefusedSave_LeavesStatusAlone()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.SeedStatus("rec1", new RecordStatus { ConsecutiveFailures = 3, LastCheckedUtc = DateTime.UtcNow });

        var moved = OrchestratorHarness.DynDnsRecord(host: string.Empty);
        Assert.Throws<ArgumentException>(() => h.Plugin.UpdateConfiguration(new PluginConfiguration { Records = new List<DNSRecord> { moved } }));
        Assert.Equal(3, h.StatusOf("rec1").ConsecutiveFailures);
    }

    [Fact]
    public void SanitizeForLog_StripsWhatXmlCannotHold()
    {
        var text = FormatUtilities.SanitizeForLog("a\u0001b￾c\uD800d\uDC00e\r\nf", 100);
        Assert.Equal("abcdef", text);
        Assert.Null(XmlText.FindInvalid(new DNSRecord { LastStatus = text }));
        Assert.Equal("[empty]", FormatUtilities.SanitizeForLog(null));
    }

    [Fact]
    public void SanitizeForLog_NeverSplitsASurrogatePair()
    {
        var face = char.ConvertFromUtf32(0x1F600);
        Assert.Equal("ab...", FormatUtilities.SanitizeForLog("ab" + face + "cd", 3));
        Assert.Equal("ab" + face + "...", FormatUtilities.SanitizeForLog("ab" + face + "cd", 4));
        Assert.Equal("ab" + face, FormatUtilities.SanitizeForLog("ab" + face, 4));
    }

    [Fact]
    public async Task UpdateRun_ProviderReplyWithACharacterXmlRefuses_IsStoredClean()
    {
        using var h = new OrchestratorHarness();
        h.SetRecords(OrchestratorHarness.DynDnsRecord());
        h.DdnsReply = (HttpStatusCode.OK, "weird￾reply");

        var outcome = await h.RunAsync();

        var message = outcome.Records.Single().Message;
        Assert.Contains("weirdreply", message, StringComparison.Ordinal);
        Assert.Null(XmlText.FindInvalid(new DNSRecord { LastStatus = h.StatusOf("rec1").LastStatus }));
    }
}
