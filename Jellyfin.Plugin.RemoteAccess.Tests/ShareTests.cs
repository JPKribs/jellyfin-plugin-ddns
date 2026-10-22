using Jellyfin.Plugin.DynamicDns.Api;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// The Dashboard's share card: the public address and its QR code.
/// </summary>
public class ShareTests
{
    [Fact]
    public void Share_AnAddress_GivesItsQrCodeAsSvg()
    {
        var info = ShareController.Create("https://media.example.com");
        Assert.Equal("https://media.example.com", info.Url);
        Assert.StartsWith("<svg", info.QrSvg.TrimStart(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("<script", info.QrSvg, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox", info.QrSvg, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Share_NoAddress_IsEmpty()
    {
        var info = ShareController.Create(string.Empty);
        Assert.Empty(info.Url);
        Assert.Empty(info.QrSvg);
    }
}
