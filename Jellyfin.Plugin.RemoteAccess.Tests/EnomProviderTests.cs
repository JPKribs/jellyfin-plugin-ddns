using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers eNom's SetDNSHost interface: success is <c>Done=true</c> in the body, and the HostName sent is
/// the hostname made relative to the base domain in Login (<c>@</c> when they are equal).
/// </summary>
public class EnomProviderTests
{
    private static DNSRecord Record() => new()
    {
        Hostname = "home.example.com",
        Login = "example.com",
        Password = "domain-pw",
        UpdateIPv4 = true,
    };

    [Fact]
    public async Task DoneTrue_IsSuccess()
    {
        var provider = new EnomProvider(
            StubHttp.Always(HttpStatusCode.OK, ";URL Interface\nDone=true\nErrCount=0"),
            NullLogger<EnomProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task DoneFalseWith200_IsFailure()
    {
        var provider = new EnomProvider(
            StubHttp.Always(HttpStatusCode.OK, "Done=false\nErrCount=1\nErr1=Domain name not found"),
            NullLogger<EnomProvider>.Instance);

        var result = await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RequestShape_HostNameIsRelativeToBaseDomain()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "Done=true");
        });
        var provider = new EnomProvider(factory, NullLogger<EnomProvider>.Instance);

        await provider.UpdateAsync(Record(), new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Equal(
            "https://dynamic.name-services.com/interface.asp?Command=SetDNSHost"
            + "&HostName=home&Zone=example.com&DomainPassword=domain-pw&Address=1.2.3.4",
            url);
    }

    [Fact]
    public async Task ApexHostname_IsSentAsAtSign()
    {
        string? url = null;
        var factory = StubHttp.Factory(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return (HttpStatusCode.OK, "Done=true");
        });
        var provider = new EnomProvider(factory, NullLogger<EnomProvider>.Instance);
        var record = Record();
        record.Hostname = "example.com";

        await provider.UpdateAsync(record, new DetectedIP { IPv4 = "1.2.3.4" }, CancellationToken.None);

        Assert.Contains("HostName=%40", url);
        Assert.Contains("Zone=example.com", url);
    }
}
