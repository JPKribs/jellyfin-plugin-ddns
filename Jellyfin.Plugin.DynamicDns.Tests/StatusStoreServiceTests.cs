using System;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the runtime status store: it round trips status to disk and returns an empty status when no
/// file exists yet, so the dashboard and the update pass never depend on the config XML for status.
/// </summary>
public class StatusStoreServiceTests
{
    private static (StatusStoreService Store, string Dir) NewStore()
    {
        var dir = Path.Join(Path.GetTempPath(), "ddns-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(dir);
        return (new StatusStoreService(paths, NullLogger<StatusStoreService>.Instance), dir);
    }

    [Fact]
    public void Load_WhenNoFile_ReturnsEmpty()
    {
        var (store, dir) = NewStore();
        try
        {
            var data = store.Load();
            Assert.NotNull(data);
            Assert.Empty(data.Records);
            Assert.Equal(string.Empty, data.DetectedIPv4);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var (store, dir) = NewStore();
        try
        {
            var data = new StatusData { DetectedIPv4 = "203.0.114.5", DetectionMessage = "note" };
            data.Records["rec1"] = new RecordStatus { LastAction = "Updated", LastSuccess = true, ConsecutiveFailures = 2 };
            store.Save(data);

            var loaded = store.Load();
            Assert.Equal("203.0.114.5", loaded.DetectedIPv4);
            Assert.Equal("note", loaded.DetectionMessage);
            Assert.True(loaded.Records.ContainsKey("rec1"));
            Assert.Equal("Updated", loaded.Records["rec1"].LastAction);
            Assert.Equal(2, loaded.Records["rec1"].ConsecutiveFailures);

            // The store file is the JSON file beside the keys, not the config XML.
            Assert.True(File.Exists(Path.Join(dir, "Jellyfin.Plugin.DynamicDns.Status.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
