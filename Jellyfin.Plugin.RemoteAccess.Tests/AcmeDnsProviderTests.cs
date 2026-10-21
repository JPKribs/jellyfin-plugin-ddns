using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The TXT records the Cloudflare and Duck DNS providers publish and remove for DNS-01.
/// </summary>
public class AcmeDnsProviderTests
{
    [Fact]
    public async Task Cloudflare_CreatesThenDeletesOnlyItsOwnTxtRecord()
    {
        var calls = new List<string>();
        var bodies = new List<string>();
        var http = StubHttp.Factory(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            calls.Add(req.Method + " " + url);
            if (req.Content is not null)
            {
                bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            if (url.Contains("/zones/?name=", System.StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"result\":[{\"id\":\"zone1\",\"name\":\"example.com\"}]}");
            }

            if (req.Method == HttpMethod.Post)
            {
                return (HttpStatusCode.OK, "{\"result\":{\"id\":\"txt1\"}}");
            }

            if (req.Method == HttpMethod.Get && url.Contains("type=TXT", System.StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, "{\"result\":[{\"id\":\"txt1\",\"content\":\"\\\"token-value\\\"\"},{\"id\":\"other\",\"content\":\"someone-else\"}]}");
            }

            return (HttpStatusCode.OK, "{\"result\":{}}");
        });
        var provider = new CloudflareProvider(http, NullLogger<CloudflareProvider>.Instance);
        var record = new DNSRecord { Hostname = "home.example.com", Zone = "example.com", Login = "token", Password = "secret" };

        var (ok, _) = await provider.SetTxtAsync(record, "_acme-challenge.media.example.com", "token-value", CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("\"type\":\"TXT\"", bodies[0], System.StringComparison.Ordinal);
        Assert.Contains("\"name\":\"_acme-challenge.media.example.com\"", bodies[0], System.StringComparison.Ordinal);
        Assert.Contains("\"content\":\"token-value\"", bodies[0], System.StringComparison.Ordinal);

        await provider.RemoveTxtAsync(record, "_acme-challenge.media.example.com", "token-value", CancellationToken.None);
        Assert.Contains(calls, c => c.StartsWith("DELETE ", System.StringComparison.Ordinal) && c.EndsWith("/dns_records/txt1", System.StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.EndsWith("/dns_records/other", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cloudflare_ReportsATokenWithoutEditRights()
    {
        var http = StubHttp.Factory(req => req.Method == HttpMethod.Post
            ? (HttpStatusCode.Forbidden, "{\"success\":false}")
            : (HttpStatusCode.OK, "{\"result\":[{\"id\":\"zone1\",\"name\":\"example.com\"}]}"));
        var provider = new CloudflareProvider(http, NullLogger<CloudflareProvider>.Instance);

        var (ok, message) = await provider.SetTxtAsync(new DNSRecord { Zone = "example.com", Login = "token", Password = "s" }, "_acme-challenge.example.com", "v", CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("DNS edit permission", message, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuckDns_SetsAndClearsTheDomainsTxtValue()
    {
        var urls = new List<string>();
        var http = StubHttp.Factory(req =>
        {
            urls.Add(req.RequestUri!.AbsoluteUri);
            return (HttpStatusCode.OK, "OK");
        });
        var provider = new DuckDnsProvider(http, NullLogger<DuckDnsProvider>.Instance);
        var record = new DNSRecord { Hostname = "myhome", Password = "tok" };

        var (ok, _) = await provider.SetTxtAsync(record, "_acme-challenge.myhome.duckdns.org", "abc", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal("https://www.duckdns.org/update?domains=myhome&token=tok&txt=abc", urls[0]);

        await provider.RemoveTxtAsync(record, "_acme-challenge.myhome.duckdns.org", "abc", CancellationToken.None);
        Assert.Equal("https://www.duckdns.org/update?domains=myhome&token=tok&txt=&clear=true", urls[1]);
    }
}
