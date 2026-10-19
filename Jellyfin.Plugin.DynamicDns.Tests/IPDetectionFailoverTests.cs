using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the multi-endpoint failover in <see cref="IPDetectionService"/>. A configured endpoint that
/// fails, times out, or returns an unusable value must be skipped so the next configured endpoint gets a
/// turn, detection only yields null once every endpoint is spent, and a blank field uses the defaults.
/// </summary>
public class IPDetectionFailoverTests
{
    // Maps a request to a canned reply based on which endpoint it hit, so failover order is observable.
    private static IHttpClientFactory Routed(Func<string, (HttpStatusCode Code, string Body)> byUrl)
        => StubHttp.Factory(req => byUrl(req.RequestUri!.AbsoluteUri));

    private static async Task<string?> DetectV4(string configured, Func<string, (HttpStatusCode, string)> byUrl)
    {
        var svc = new IPDetectionService(Routed(byUrl), NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = configured, IPv6DetectionUrl = string.Empty };
        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);
        return ip.IPv4;
    }

    [Fact]
    public async Task ConfiguredEndpointDown_FailsOverToNextConfigured()
    {
        // The first configured endpoint errors. The next configured endpoint answers with a public IP.
        var result = await DetectV4("https://primary.test\nhttps://secondary.test", url =>
            url.Contains("primary.test", StringComparison.Ordinal)
                ? (HttpStatusCode.InternalServerError, "down")
                : (HttpStatusCode.OK, "203.0.114.5"));

        Assert.Equal("203.0.114.5", result);
    }

    [Fact]
    public async Task FirstConfiguredGarbage_SecondConfiguredWins_BeforeFallbacks()
    {
        // Two configured endpoints separated by a newline. The first returns a non address, the second a
        // good one, and it must be used before any built in fallback is consulted.
        var result = await DetectV4("https://a.test\nhttps://b.test", url =>
            url.Contains("a.test", StringComparison.Ordinal) ? (HttpStatusCode.OK, "not-an-ip")
            : url.Contains("b.test", StringComparison.Ordinal) ? (HttpStatusCode.OK, "8.8.4.4")
            : throw new Xunit.Sdk.XunitException("fallback should not be reached: " + url));

        Assert.Equal("8.8.4.4", result);
    }

    [Fact]
    public async Task PrivateAddressFromConfigured_FailsOverToNextConfigured()
    {
        // A configured endpoint that returns a private address is rejected by the public filter, so the
        // next configured endpoint is tried.
        var result = await DetectV4("https://primary.test\nhttps://secondary.test", url =>
            url.Contains("primary.test", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, "10.0.0.1")
                : (HttpStatusCode.OK, "1.1.1.1"));

        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public async Task TwoEndpointsAgree_AcceptsTheAddress()
    {
        // Two endpoints return the same address, so it is accepted.
        var result = await DetectV4("https://a.test\nhttps://b.test", _ => (HttpStatusCode.OK, "203.0.114.5"));
        Assert.Equal("203.0.114.5", result);
    }

    [Fact]
    public async Task EndpointsDisagree_PublishesNothing()
    {
        // Two endpoints return different addresses and none agrees, so nothing is published.
        var result = await DetectV4("https://a.test\nhttps://b.test", url =>
            url.Contains("a.test", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, "203.0.114.5")
                : (HttpStatusCode.OK, "8.8.4.4"));
        Assert.Null(result);
    }

    [Fact]
    public async Task HttpEndpoint_IsRefused()
    {
        // A plain http endpoint is refused, so a lone http endpoint detects nothing.
        var result = await DetectV4("http://insecure.test", _ => (HttpStatusCode.OK, "203.0.114.5"));
        Assert.Null(result);
    }

    [Fact]
    public async Task EveryEndpointFails_ReturnsNull()
    {
        var result = await DetectV4("https://primary.test", _ => (HttpStatusCode.InternalServerError, "down"));
        Assert.Null(result);
    }

    [Fact]
    public async Task BlankConfiguration_UsesBuiltInFallbacks()
    {
        // With no configured endpoint, detection must still work off the built-in list alone.
        var result = await DetectV4(string.Empty, url =>
            url.Contains("ipify.org", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, "9.9.9.9")
                : (HttpStatusCode.InternalServerError, "down"));

        Assert.Equal("9.9.9.9", result);
    }

    [Fact]
    public async Task OnlyInternalAddresses_ReturnsNoteFlaggingInternal()
    {
        // Every endpoint answers with a private address. Detection yields no IP and a note that calls out
        // the internal case so the dashboard can explain why nothing was published.
        var svc = new IPDetectionService(
            Routed(_ => (HttpStatusCode.OK, "192.168.1.50")),
            NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://primary.test", IPv6DetectionUrl = string.Empty };

        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);

        Assert.Null(ip.IPv4);
        Assert.NotNull(ip.IPv4Note);
        Assert.Contains("internal", ip.IPv4Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreachableEndpoints_ReturnsPlainFailureNote()
    {
        // When endpoints simply fail rather than returning an internal address, the note describes a
        // detection failure and does not claim the address looked internal.
        var svc = new IPDetectionService(
            Routed(_ => (HttpStatusCode.InternalServerError, "down")),
            NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://primary.test", IPv6DetectionUrl = string.Empty };

        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);

        Assert.Null(ip.IPv4);
        Assert.NotNull(ip.IPv4Note);
        Assert.Contains("detection failed", ip.IPv4Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("https://host.test:99999")]
    [InlineData("https://[bad")]
    [InlineData("https:///nohost")]
    public async Task MalformedConfiguredEndpoint_FailsAlone_AndTheNextOneIsUsed(string malformed)
    {
        // A malformed endpoint used to throw out of detection and abort the whole run.
        var result = await DetectV4(malformed + "\nhttps://good.test", url =>
            url.Contains("good.test", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, "8.8.4.4")
                : throw new Xunit.Sdk.XunitException("unexpected request: " + url));

        Assert.Equal("8.8.4.4", result);
    }

    [Fact]
    public async Task OversizedReply_IsReadOnlyUpToTheCap()
    {
        var body = new CountingStream(10 * 1024 * 1024);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StreamHandler(req =>
            req.RequestUri!.Host == "huge.test" ? new StreamContent(body) : new StringContent("8.8.4.4"))));
        var svc = new IPDetectionService(factory, NullLogger<IPDetectionService>.Instance);
        var cfg = new PluginConfiguration { IPv4DetectionUrl = "https://huge.test\nhttps://good.test", IPv6DetectionUrl = string.Empty };

        var ip = await svc.DetectAsync(cfg, needIPv4: true, needIPv6: false, CancellationToken.None);

        Assert.Equal("8.8.4.4", ip.IPv4);
        Assert.True(body.BytesRead < 64 * 1024, "read " + body.BytesRead + " bytes");
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpContent> _content;

        public StreamHandler(Func<HttpRequestMessage, HttpContent> content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content(request) });
    }

    // An endless-looking reply of 'x' bytes that counts how much was actually read.
    private sealed class CountingStream : System.IO.Stream
    {
        private readonly long _length;

        public CountingStream(long length) => _length = length;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _length - BytesRead);
            Array.Fill(buffer, (byte)'x', offset, n);
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
