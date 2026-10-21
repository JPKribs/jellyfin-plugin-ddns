using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// How failures are logged: once for a fault that repeats, by how much it matters, and with a line when it
/// clears. Also the history caps and the FreeDNS update links.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class LoggingCleanupTests
{
    [Fact]
    public void RepeatedProblem_WarnsOnceThenSaysWhenItClears()
    {
        var problem = new RepeatedProblem();
        var levels = new List<LogLevel>();
        var recovered = 0;
        problem.Succeeded(() => recovered++);
        problem.Failed(levels.Add);
        problem.Failed(levels.Add);
        problem.Succeeded(() => recovered++);
        problem.Succeeded(() => recovered++);
        problem.Failed(levels.Add);

        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Debug, LogLevel.Warning }, levels);
        Assert.Equal(1, recovered);
    }

    private static OrchestratorHarness ForcedRefresh(bool succeed)
    {
        var h = new OrchestratorHarness(new FakeProvider(
            DNSProviderKind.Cloudflare,
            (_, _, _) => Task.FromResult(succeed ? DNSUpdateResult.Ok("ok") : DNSUpdateResult.Fail("provider said no"))));
        h.Plugin.MutateConfiguration(c =>
        {
            c.ForceUpdateHours = 1;
            return false;
        });
        h.SetRecords(new DNSRecord { Id = "cf", Provider = DNSProviderKind.Cloudflare, Hostname = "home.example.com", Password = "t", UpdateIPv6 = false });
        h.LookupAnswer = new[] { IPAddress.Parse(h.PublicIPv4) };
        h.SeedStatus("cf", new RecordStatus { LastIPv4 = h.PublicIPv4, LastSuccess = !succeed, LastUpdateUtc = DateTime.UtcNow.AddDays(-2), ConsecutiveFailures = succeed ? 2 : 0 });
        return h;
    }

    [Fact]
    public async Task DdnsFailure_WhileDnsAlreadyServesTheAddress_IsAWarning()
    {
        using var h = ForcedRefresh(succeed: false);
        await h.RunAsync();
        var entry = Assert.Single(h.Activity, a => a.Type == "DynamicDNS.RecordFailed");
        Assert.Equal(LogLevel.Warning, entry.LogSeverity);
    }

    [Fact]
    public async Task DdnsSuccess_AfterFailures_IsLoggedAsRecovered()
    {
        using var h = ForcedRefresh(succeed: true);
        await h.RunAsync();
        Assert.Contains(h.Activity, a => a.Type == "DynamicDNS.RecordRecovered");
    }

    [Theory]
    [InlineData("https://freedns.afraid.org/dynamic/update.php?abc", true)]
    [InlineData("http://freedns.afraid.org/dynamic/update.php?abc", false)]
    [InlineData("https://attacker.example/steal?abc", false)]
    [InlineData("not a url", false)]
    public void FreeDns_FollowsOnlyLinksBackToItsServer(string link, bool trusted)
        => Assert.Equal(trusted, FreeDnsProvider.IsTrustedUpdateUrl(link, "https://freedns.afraid.org"));

    [Fact]
    public void History_ClientReportedNamesAreCapped()
    {
        var dir = Path.Join(Path.GetTempPath(), "people-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new PeopleStore(new ProxyPaths(dir), NullLogger<PeopleStore>.Instance);
            var huge = new string('x', 5000);
            store.RecordObservation(new Observation { UserId = "u", UserName = huge, Ip = "203.0.113.5", Host = huge, DeviceId = huge, DeviceName = huge, Client = huge, LastSeenUtc = DateTime.UtcNow });
            var row = store.GetActivity(10).Single();
            Assert.True(row.User.Length <= 256);
            Assert.True(row.Host.Length <= 256);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
