using System.Linq;
using System.Net;
using Jellyfin.Plugin.DynamicDns.Providers;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Pins which providers declare themselves IPv4-only. The update cycle clears the record's IPv6 flag
/// for these, and the dashboard hides the AAAA toggle, so an address the protocol cannot carry is never
/// recorded as pushed. The declaration and the dashboard field must agree.
/// </summary>
public class ProviderIPv6SupportTests
{
    [Fact]
    public void SingleAddressProtocols_DeclareIPv4Only()
    {
        var factory = StubHttp.Always(HttpStatusCode.OK, string.Empty);

        IDNSProvider[] v4Only =
        {
            new OvhProvider(factory, NullLogger<OvhProvider>.Instance),
            new DynDns1Provider(factory, NullLogger<DynDns1Provider>.Instance),
            new DslReports1Provider(factory, NullLogger<DslReports1Provider>.Instance),
            new NfsnProvider(factory, NullLogger<NfsnProvider>.Instance)
        };

        Assert.All(v4Only, p => Assert.False(p.SupportsIPv6));
        Assert.All(v4Only, p => Assert.False(p.Fields.IPv6));
    }

    [Fact]
    public void DualStackProviders_KeepTheDefault()
    {
        var factory = StubHttp.Always(HttpStatusCode.OK, string.Empty);
        var cloudflare = new CloudflareProvider(factory, NullLogger<CloudflareProvider>.Instance);
        var yandex = new YandexProvider(factory, NullLogger<YandexProvider>.Instance);

        Assert.True(cloudflare.SupportsIPv6);
        Assert.True(cloudflare.Fields.IPv6);
        Assert.True(yandex.SupportsIPv6);
        Assert.True(yandex.Fields.IPv6);
    }
}
