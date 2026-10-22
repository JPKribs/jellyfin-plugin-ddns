using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.People;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The location database is only ever replaced by one that works. A failed download keeps the one in use, and a
/// database that will not open falls back to the one it replaced.
/// </summary>
public sealed class GeoIpFallbackTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "geoip-" + Guid.NewGuid().ToString("N"));

    public GeoIpFallbackTests() => Directory.CreateDirectory(Path.Join(_dir, "GeoIP"));

    private string Target => Path.Join(_dir, "GeoIP", "dbip-country-lite.mmdb");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // A memory-mapped file still open is left for the system's temporary-file cleanup.
        }
    }

    [Fact]
    public async Task DamagedDatabase_FallsBackToThePreviousOne_WithoutDownloading()
    {
        File.WriteAllBytes(Target, Encoding.ASCII.GetBytes("not a database"));
        File.WriteAllBytes(Target + ".previous", CountryDatabase("US"));
        var http = Substitute.For<IHttpClientFactory>();
        using var geo = new GeoIpService(new ProxyPaths(_dir), http, NullLogger<GeoIpService>.Instance);

        await geo.EnsureAsync(LocationDetail.Country, CancellationToken.None);

        Assert.True(geo.IsLoaded);
        Assert.Equal("US", geo.Lookup(IPAddress.Parse("8.8.8.8")).CountryCode);
        Assert.True(File.Exists(Target + ".previous"));
        http.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    [Fact]
    public async Task FailedDownload_KeepsTheDatabaseInUse()
    {
        File.WriteAllBytes(Target, CountryDatabase("US"));
        var before = File.ReadAllBytes(Target);
        using var geo = new GeoIpService(new ProxyPaths(_dir), Http(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)), NullLogger<GeoIpService>.Instance);
        await geo.EnsureAsync(LocationDetail.Country, CancellationToken.None);

        var updated = await geo.UpdateAsync(LocationDetail.Country, CancellationToken.None);

        Assert.False(updated);
        Assert.True(geo.IsLoaded);
        Assert.Equal("US", geo.Lookup(IPAddress.Parse("8.8.8.8")).CountryCode);
        Assert.Equal(before, File.ReadAllBytes(Target));
        Assert.Contains("500", geo.GetStatus(LocationDetail.Country).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidDownload_KeepsTheDatabaseInUse()
    {
        File.WriteAllBytes(Target, CountryDatabase("US"));
        var before = File.ReadAllBytes(Target);
        using var geo = new GeoIpService(new ProxyPaths(_dir), Http(() => Gzip(Encoding.ASCII.GetBytes("garbage"))), NullLogger<GeoIpService>.Instance);
        await geo.EnsureAsync(LocationDetail.Country, CancellationToken.None);

        Assert.False(await geo.UpdateAsync(LocationDetail.Country, CancellationToken.None));

        Assert.True(geo.IsLoaded);
        Assert.Equal(before, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".download"));
    }

    private static IHttpClientFactory Http(Func<HttpResponseMessage> respond)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Responder(respond)));
        return factory;
    }

    private sealed class Responder : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public Responder(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond());
    }

    private static HttpResponseMessage Gzip(byte[] body)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            gzip.Write(body);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }

    // The smallest MaxMind DB that maps every IPv4 address to one country: a single search tree node whose two
    // records both point at the only data record, then the data section and the metadata.
    private static byte[] CountryDatabase(string isoCode)
    {
        var data = new List<byte>();
        Map(data, 1);
        Text(data, "country");
        Map(data, 2);
        Text(data, "iso_code");
        Text(data, isoCode);
        Text(data, "names");
        Map(data, 1);
        Text(data, "en");
        Text(data, "Country " + isoCode);

        const int NodeCount = 1;
        var pointer = NodeCount + 16;
        var file = new List<byte>();
        for (var side = 0; side < 2; side++)
        {
            file.Add((byte)(pointer >> 16));
            file.Add((byte)(pointer >> 8));
            file.Add((byte)pointer);
        }

        file.AddRange(new byte[16]);
        file.AddRange(data);
        file.AddRange(new byte[] { 0xAB, 0xCD, 0xEF });
        file.AddRange(Encoding.ASCII.GetBytes("MaxMind.com"));

        var meta = new List<byte>();
        Map(meta, 9);
        Text(meta, "binary_format_major_version");
        Unsigned(meta, 5, 2);
        Text(meta, "binary_format_minor_version");
        Unsigned(meta, 5, 0);
        Text(meta, "build_epoch");
        meta.Add(0x04);
        meta.Add(0x02);
        meta.AddRange(new byte[] { 0x69, 0x00, 0x00, 0x00 });
        Text(meta, "database_type");
        Text(meta, "DBIP-Country-Lite");
        Text(meta, "description");
        Map(meta, 0);
        Text(meta, "ip_version");
        Unsigned(meta, 5, 4);
        Text(meta, "languages");
        meta.Add(0x01);
        meta.Add(0x04);
        Text(meta, "en");
        Text(meta, "node_count");
        Unsigned(meta, 6, NodeCount);
        Text(meta, "record_size");
        Unsigned(meta, 5, 24);
        file.AddRange(meta);
        return file.ToArray();
    }

    private static void Map(List<byte> into, int entries) => into.Add((byte)((7 << 5) | entries));

    private static void Text(List<byte> into, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        into.Add((byte)((2 << 5) | bytes.Length));
        into.AddRange(bytes);
    }

    private static void Unsigned(List<byte> into, int type, int value)
    {
        into.Add((byte)((type << 5) | 1));
        into.Add((byte)value);
    }
}
