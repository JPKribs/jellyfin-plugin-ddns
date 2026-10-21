using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The order the takeover writes in, so a crash at any point still leaves Undo what it needs.
/// </summary>
public sealed class OrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "orchestrator-" + Guid.NewGuid().ToString("N"));
    private readonly IConfigurationManager _configuration = Substitute.For<IConfigurationManager>();

    public OrchestratorTests()
    {
        Directory.CreateDirectory(_dir);
        _configuration.CommonApplicationPaths.ConfigurationDirectoryPath.Returns(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private JellyfinNetworkOrchestrator Orchestrator() => new(
        _configuration,
        Substitute.For<ISystemManager>(),
        Substitute.For<IHostApplicationLifetime>(),
        new ProxyPaths(_dir),
        null,
        NullLogger<JellyfinNetworkOrchestrator>.Instance);

    private static ProxySettings Settings(bool loopbackOnly) => new()
    {
        Enabled = true,
        Hostnames = new List<string> { "media.example.com" },
        Advanced = new ProxyAdvancedSettings { JellyfinLoopbackOnly = loopbackOnly }
    };

    private static NetworkConfiguration Network() => new() { EnableIPv4 = true, LocalNetworkAddresses = new[] { "192.168.1.10" } };

    [Fact]
    public void Commit_SavesACopyOfJellyfinsNetworkFileOnce()
    {
        File.WriteAllText(Path.Join(_dir, "network.xml"), "original");
        var orchestrator = Orchestrator();
        orchestrator.Commit(Settings(false), Network());
        File.WriteAllText(Path.Join(_dir, "network.xml"), "changed");
        orchestrator.Commit(Settings(true), Network());

        Assert.Equal("original", File.ReadAllText(Path.Join(_dir, JellyfinNetworkOrchestrator.BackupFileName)));
    }

    [Fact]
    public void Commit_TurningLoopbackOff_KeepsTheRecordUntilJellyfinsSettingsAreSaved()
    {
        var orchestrator = Orchestrator();
        var network = Network();
        orchestrator.Commit(Settings(true), network);
        Assert.True(orchestrator.State.Snapshot!.BoundToLoopback);

        // Jellyfin's settings fail to save, as a crash at that moment would leave them.
        var fail = true;
        _configuration.When(c => c.SaveConfiguration("network", Arg.Any<object>())).Do(_ =>
        {
            if (fail)
            {
                throw new IOException("disk full");
            }
        });
        Assert.Throws<IOException>(() => orchestrator.Commit(Settings(false), network));
        Assert.True(orchestrator.State.Snapshot!.BoundToLoopback);
        Assert.NotEmpty(orchestrator.State.Snapshot!.WrittenLocal);

        fail = false;
        orchestrator.Commit(Settings(false), network);
        Assert.False(orchestrator.State.Snapshot!.BoundToLoopback);
        Assert.Equal(new[] { "192.168.1.10", "127.0.0.1" }, network.LocalNetworkAddresses);
    }
}
