using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Per-route settings: a route's own value wins, an empty one inherits the global value, and Jellyfin
/// itself always uses the global settings.
/// </summary>
public class RouteOverridesTests
{
    private static readonly JellyfinNetworkView Jellyfin = new(new Uri("http://127.0.0.1:8097/"), string.Empty, true, 8097, 0);

    private static ProxyRuntime Runtime(Action<RouteOverrides>? edit = null)
    {
        var s = new ProxySettings { Enabled = true, Hostnames = new List<string> { "media.example.com" } };
        var route = new ProxyRoute { Id = "svc", Hostnames = new List<string> { "svc.example.com" }, Target = "http://10.0.0.2" };
        edit?.Invoke(route.Overrides);
        s.Routes.Add(route);
        s.Advanced.MaxRequestBodyMbWan = 100;
        s.Advanced.MaxRequestBodyMbLan = 0;
        s.Advanced.BackendActivityTimeoutSeconds = 300;
        ProxySettingsValidator.Normalize(s);
        return ProxyRuntimeFactory.Build(s, Jellyfin, _ => false, httpsAvailable: true);
    }

    [Fact]
    public void EmptyOverrides_Inherit()
    {
        var runtime = Runtime();
        var route = runtime.Routes.Single();
        Assert.Equal(100, EffectiveSettings.MaxRequestBodyMb(runtime, route, local: false));
        Assert.Equal(0, EffectiveSettings.MaxRequestBodyMb(runtime, route, local: true));
        Assert.Equal(300, EffectiveSettings.BackendActivityTimeoutSeconds(runtime, route));
        Assert.Equal(180, EffectiveSettings.HstsMaxAgeDays(runtime, route));
        Assert.True(EffectiveSettings.SameOriginFramesOnly(runtime, route));
    }

    [Fact]
    public void Overrides_Win()
    {
        var runtime = Runtime(o =>
        {
            o.MaxRequestBodyMbWan = 5;
            o.MaxRequestBodyMbLan = 2000;
            o.BackendActivityTimeoutSeconds = 3600;
            o.HstsEnabled = false;
            o.SameOriginFramesOnly = false;
        });
        var route = runtime.Routes.Single();
        Assert.Equal(5, EffectiveSettings.MaxRequestBodyMb(runtime, route, local: false));
        Assert.Equal(2000, EffectiveSettings.MaxRequestBodyMb(runtime, route, local: true));
        Assert.Equal(3600, EffectiveSettings.BackendActivityTimeoutSeconds(runtime, route));
        Assert.Null(EffectiveSettings.HstsMaxAgeDays(runtime, route));
        Assert.False(EffectiveSettings.SameOriginFramesOnly(runtime, route));

        // Jellyfin is untouched by a route's overrides.
        Assert.Equal(100, EffectiveSettings.MaxRequestBodyMb(runtime, null, local: false));
        Assert.Equal(180, EffectiveSettings.HstsMaxAgeDays(runtime, null));
    }

    [Fact]
    public void HstsDaysOverride_OnlyMattersWhenOn()
    {
        var runtime = Runtime(o => o.HstsMaxAgeDays = 30);
        Assert.Equal(30, EffectiveSettings.HstsMaxAgeDays(runtime, runtime.Routes.Single()));
    }

    [Fact]
    public void MinReadRate_OffForRoutesUnlessSet_MediaExemptForJellyfin()
    {
        var runtime = Runtime();
        Assert.Null(EffectiveSettings.MinResponseBytesPerSecond(runtime, runtime.Routes.Single(), "/api"));
        Assert.Equal(240, EffectiveSettings.MinResponseBytesPerSecond(runtime, null, "/Items"));
        Assert.Null(EffectiveSettings.MinResponseBytesPerSecond(runtime, null, "/Videos/1/stream"));

        var set = Runtime(o => o.MinResponseBytesPerSecond = 500);
        Assert.Equal(500, EffectiveSettings.MinResponseBytesPerSecond(set, set.Routes.Single(), "/api"));
    }

    [Fact]
    public void Timeout_ReachesTheRoutesCluster()
    {
        var runtime = Runtime(o => o.BackendActivityTimeoutSeconds = 900);
        var clusters = ProxyConfigProvider.BuildClusters(runtime);
        Assert.Equal(TimeSpan.FromSeconds(900), clusters.Single(c => c.ClusterId == "route-svc").HttpRequest!.ActivityTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), clusters.Single(c => c.ClusterId == ProxyRuntime.JellyfinId).HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public void Normalize_ClampsOverrides()
    {
        var s = new ProxySettings();
        s.Routes.Add(new ProxyRoute { Hostnames = new List<string> { "a.example.com" }, Target = "http://10.0.0.2", Overrides = new RouteOverrides { BackendActivityTimeoutSeconds = 5, MaxRequestBodyMbWan = -1, HstsMaxAgeDays = 9999 } });
        s.Routes.Add(new ProxyRoute { Hostnames = new List<string> { "b.example.com" }, Target = "http://10.0.0.3", Overrides = null! });
        ProxySettingsValidator.Normalize(s);

        var o = s.Routes[0].Overrides;
        Assert.Equal(61, o.BackendActivityTimeoutSeconds);
        Assert.Equal(0, o.MaxRequestBodyMbWan);
        Assert.Equal(730, o.HstsMaxAgeDays);
        Assert.NotNull(s.Routes[1].Overrides);
        Assert.Null(s.Routes[1].Overrides.MaxRequestBodyMbLan);
    }
}
