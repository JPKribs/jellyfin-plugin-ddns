using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Jellyfin.Plugin.DynamicDns.Proxy.Network;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The request policy: what internet and LAN clients may reach, and how plain HTTP from the internet is handled.
/// </summary>
public class ProxyPolicyTests
{
    private static readonly JellyfinNetworkView Jellyfin = new(new Uri("http://127.0.0.1:8097/"), string.Empty, true, 8097, 0);

    internal static ProxySettings Settings(Action<ProxySettings>? edit = null)
    {
        var s = new ProxySettings { Enabled = true, Hostnames = new List<string> { "media.example.com" } };
        edit?.Invoke(s);
        ProxySettingsValidator.Normalize(s);
        return s;
    }

    private static ProxyRuntime Runtime(ProxySettings? settings = null, JellyfinNetworkView? jellyfin = null)
        => ProxyRuntimeFactory.Build(settings ?? Settings(), jellyfin ?? Jellyfin, _ => false, httpsAvailable: true);

    private static RequestDecision Decide(bool local, bool https, string host, string path, string method = "GET", ProxyRuntime? runtime = null, Http01ChallengeStore? challenges = null)
    {
        challenges ??= new Http01ChallengeStore();
        return RequestPolicy.Decide(runtime ?? Runtime(), new RequestFacts(local, https, host, method, path, QueryString.Empty), challenges);
    }

    [Fact]
    public void Wan_HttpsConfiguredHost_Forwards()
        => Assert.Equal(RequestAction.Forward, Decide(false, true, "media.example.com", "/web/index.html").Action);

    [Fact]
    public void Wan_UnknownHost_Dropped()
    {
        Assert.Equal(RequestAction.Drop, Decide(false, true, "203.0.113.5", "/").Action);
        Assert.Equal(RequestAction.Drop, Decide(false, true, "other.example.com", "/").Action);
    }

    [Fact]
    public void Wan_PlainHttp_RedirectsToTheExternalHttpsPort()
    {
        var redirect = Decide(false, false, "media.example.com", "/web/index.html", runtime: Runtime(Settings(s => s.Advanced.HttpsPort = 8443)));
        Assert.Equal(RequestAction.Redirect, redirect.Action);
        // The internet reaches HTTPS on 443 whatever port the proxy listens on, so the address has no port.
        Assert.Equal("https://media.example.com/web/index.html", redirect.Value);

        // Without a certificate there is no HTTPS to send anyone to, so only certificate checks are answered.
        var noHttps = ProxyRuntimeFactory.Build(Settings(), Jellyfin, _ => false, httpsAvailable: false);
        Assert.Equal(RequestAction.Drop, Decide(false, false, "media.example.com", "/web/index.html", runtime: noHttps).Action);

        // The bare IP and names the proxy does not serve get nothing.
        Assert.Equal(RequestAction.Drop, Decide(false, false, "203.0.113.5", "/").Action);
        Assert.Equal(RequestAction.Drop, Decide(false, false, "other.example.com", "/").Action);
    }

    [Fact]
    public void Wan_PlainHttp_ServesChallengeAndRedirects()
    {
        var challenges = new Http01ChallengeStore();
        challenges.Add("tok", "tok.key");
        var runtime = Runtime();

        var challenge = Decide(false, false, "media.example.com", "/.well-known/acme-challenge/tok", runtime: runtime, challenges: challenges);
        Assert.Equal(RequestAction.Challenge, challenge.Action);
        Assert.Equal("tok.key", challenge.Value);

        Assert.Equal(RequestAction.Drop, Decide(false, false, "media.example.com", "/.well-known/acme-challenge/other", runtime: runtime, challenges: challenges).Action);

        var redirect = Decide(false, false, "media.example.com", "/web/index.html", runtime: runtime, challenges: challenges);
        Assert.Equal(RequestAction.Redirect, redirect.Action);
        Assert.Equal("https://media.example.com/web/index.html", redirect.Value);

        // Never redirect to a name the proxy does not serve.
        Assert.Equal(RequestAction.Drop, Decide(false, false, "evil.example.net", "/", runtime: runtime, challenges: challenges).Action);
    }

    [Fact]
    public void Challenge_IsAnsweredForALocalClientToo()
    {
        // A router or container that rewrites the source of forwarded connections makes Let's Encrypt look local.
        var challenges = new Http01ChallengeStore();
        challenges.Add("tok", "tok.key");
        var local = Decide(true, false, "media.example.com", "/.well-known/acme-challenge/tok", challenges: challenges);
        Assert.Equal(RequestAction.Challenge, local.Action);
        Assert.Equal("tok.key", local.Value);

        // An unknown token from the internet is still dropped, not redirected.
        Assert.Equal(RequestAction.Drop, Decide(false, false, "media.example.com", "/.well-known/acme-challenge/none", challenges: challenges).Action);
    }

    [Fact]
    public void Wildcard_EveryMatchingNameReachesJellyfin()
    {
        var runtime = Runtime(Settings(s => s.Hostnames = new List<string> { "*.example.com" }));
        Assert.Equal(RequestAction.Forward, Decide(false, true, "media.example.com", "/web/index.html", runtime: runtime).Action);
        Assert.Equal(RequestAction.Forward, Decide(false, true, "photos.example.com", "/web/index.html", runtime: runtime).Action);
        Assert.Equal(RequestAction.Drop, Decide(false, true, "example.org", "/", runtime: runtime).Action);
    }

    [Fact]
    public void Media_GetsALongIdleTimer()
    {
        // A paused player stops reading, which stalls the copy; media must not be cut off after five minutes.
        var runtime = Runtime();
        var clusters = ProxyConfigProvider.BuildClusters(runtime);
        var media = Assert.Single(clusters, c => c.ClusterId == ProxyRuntime.JellyfinMediaId);
        Assert.True(media.HttpRequest!.ActivityTimeout >= TimeSpan.FromHours(1));
        Assert.Contains(ProxyConfigProvider.BuildRoutes(runtime), r => r.RouteId == ProxyRuntime.JellyfinMediaId);
    }

    [Fact]
    public void Redirect_KeepsPathAndQuery()
        => Assert.Equal("https://media.example.com/a?b=1", RequestPolicy.HttpsUrl("media.example.com", "/a", new QueryString("?b=1")));

    [Fact]
    public void Wan_StartupAlwaysForbidden()
        => Assert.Equal(RequestAction.Forbidden, Decide(false, true, "media.example.com", "/Startup/Configuration").Action);

    [Fact]
    public void Wan_StartupForbiddenUnderBaseUrl()
    {
        var jellyfin = Jellyfin with { BaseUrl = "/jellyfin" };
        var runtime = Runtime(jellyfin: jellyfin);
        Assert.Equal(RequestAction.Forbidden, Decide(false, true, "media.example.com", "/jellyfin/Startup/User", runtime: runtime).Action);
    }

    [Fact]
    public void Wan_AdminApisAreJellyfinsToGuard()
        => Assert.Equal(RequestAction.Forward, Decide(false, true, "media.example.com", "/System/Configuration").Action);

    [Fact]
    public void Wan_UserList_IsJellyfinsAnswerUnchanged()
    {
        // The proxy never rewrites what Jellyfin sends. Which users show on sign-in screens is Jellyfin's setting.
        Assert.Equal(RequestAction.Forward, Decide(false, true, "media.example.com", "/Users/Public").Action);
        Assert.Equal(RequestAction.Forward, Decide(true, false, "192.168.1.10", "/Users/Public").Action);
    }

    [Fact]
    public void Wan_RefusedWhileJellyfinIgnoresForwardingHeaders()
    {
        var untrusting = Runtime(jellyfin: Jellyfin with { TrustsForwarding = false });
        Assert.Equal(RequestAction.Drop, Decide(false, true, "media.example.com", "/web/index.html", runtime: untrusting).Action);

        // The LAN is unaffected.
        Assert.Equal(RequestAction.Forward, Decide(true, false, "192.168.1.10", "/web/index.html", runtime: untrusting).Action);
    }

    [Fact]
    public void Lan_AnyHostReachesJellyfin()
    {
        var decision = Decide(true, false, "192.168.1.10", "/web/index.html");
        Assert.Equal(RequestAction.Forward, decision.Action);
    }
}
