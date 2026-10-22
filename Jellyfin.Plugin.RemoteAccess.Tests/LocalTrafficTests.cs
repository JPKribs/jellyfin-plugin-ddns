using System;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Proxy.Hosting;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Noticing when every visitor looks local.
/// </summary>
public class LocalTrafficTests
{
    private static readonly IPAddress _router = IPAddress.Parse("172.17.0.1");
    private static readonly DateTime _start = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static int Feed(LocalTrafficWatch watch, int count, IPAddress client, DateTime at, bool publicName = true)
    {
        var reported = 0;
        for (var i = 0; i < count; i++)
        {
            reported += watch.Observe(true, publicName, client, at.AddSeconds(i)) ? 1 : 0;
        }

        return reported;
    }

    [Fact]
    public void Watch_ManyPublicRequestsFromOneLocalAddress_IsReportedOnce()
    {
        var calls = 0;
        var watch = new LocalTrafficWatch(() => calls++);
        Assert.Equal(0, Feed(watch, LocalTrafficWatch.Threshold - 1, _router, _start));
        Assert.False(watch.Suspected);

        Assert.Equal(1, Feed(watch, 50, _router, _start.AddMinutes(5)));
        Assert.True(watch.Suspected);

        // A later hour that shows it again does not log again.
        Assert.Equal(0, Feed(watch, 200, _router, _start.AddHours(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Watch_InternetVisitorsCarryingMost_ClearIt()
    {
        var watch = new LocalTrafficWatch();
        Feed(watch, 150, _router, _start);
        Assert.True(watch.Suspected);

        // One internet visitor no longer clears it, since the local address may hide only some visitors.
        watch.Observe(false, true, IPAddress.Parse("203.0.113.9"), _start.AddMinutes(10));
        Assert.True(watch.Suspected);

        // Once the internet carries most of the hour, the local address is one client among many.
        for (var i = 0; i < 200; i++)
        {
            watch.Observe(false, true, IPAddress.Parse("203.0.113.9"), _start.AddMinutes(11));
        }

        Assert.False(watch.Suspected);
    }

    [Fact]
    public void Watch_SeveralLocalAddressesOrLocalNames_AreNormal()
    {
        var watch = new LocalTrafficWatch();
        Feed(watch, 80, _router, _start);
        Feed(watch, 80, IPAddress.Parse("192.168.1.30"), _start.AddMinutes(1));
        Assert.False(watch.Suspected);

        // Requests for a local name, such as the server's own address, say nothing about visitors.
        var names = new LocalTrafficWatch();
        Feed(names, 300, _router, _start, publicName: false);
        Assert.False(names.Suspected);
    }

    [Fact]
    public void Watch_AQuietHour_ClearsItAtTheNextHour()
    {
        var watch = new LocalTrafficWatch();
        Feed(watch, 150, _router, _start);
        Feed(watch, 10, _router, _start.AddMinutes(61));
        Assert.True(watch.Suspected);

        // The hour after 12:01 saw only ten requests, so at 13:02 the sign no longer shows.
        Feed(watch, 1, _router, _start.AddMinutes(123));
        Assert.False(watch.Suspected);
    }
}
