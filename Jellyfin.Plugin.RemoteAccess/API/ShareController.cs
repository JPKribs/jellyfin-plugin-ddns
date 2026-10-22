using System.Linq;
using Jellyfin.Plugin.DynamicDns.Proxy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Jellyfin.Plugin.DynamicDns.Api;

/// <summary>
/// The server's public address, with a QR code to scan into the Jellyfin apps, for the Dashboard's share card.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("DynamicDns/Share")]
[Produces("application/json")]
public class ShareController : ControllerBase
{
    /// <summary>
    /// Returns the address people use and its QR code: the first public name, or else a name only the home network
    /// knows, marked as such. Empty while Remote Access is off or has no exact name.
    /// </summary>
    /// <returns>The address and the QR code as SVG markup.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ShareInfo> Get()
    {
        var hostnames = Plugin.Instance?.ReadConfiguration(c => c.Proxy.Enabled ? c.Proxy.Hostnames.ToList() : new System.Collections.Generic.List<string>())
            ?? new System.Collections.Generic.List<string>();
        var publicName = hostnames.FirstOrDefault(h => !h.StartsWith('*') && HostnameMatcher.IsPublicName(h));
        var name = publicName ?? hostnames.FirstOrDefault(h => !h.StartsWith('*'));
        var info = Create(name is null ? string.Empty : "https://" + name);
        info.HomeOnly = name is not null && publicName is null;
        return Ok(info);
    }

    /// <summary>
    /// Builds the share card's contents for an address.
    /// </summary>
    /// <param name="url">The public address, empty when there is none.</param>
    /// <returns>The address and its QR code, both empty for no address.</returns>
    internal static ShareInfo Create(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return new ShareInfo();
        }

        // Plain black on white with a quiet zone, the form phone cameras read most reliably, whatever the theme.
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        // Sized by its view box, so the page can scale it to any size without blurring.
        using var code = new SvgQRCode(data);
        var svg = code.GetGraphic(8, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
        return new ShareInfo { Url = url, QrSvg = svg };
    }

}

/// <summary>
/// What the Dashboard's share card shows.
/// </summary>
public sealed class ShareInfo
{
    /// <summary>Gets or sets the address people use from the internet, empty when there is none.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the address as a QR code in SVG markup, empty when there is none.</summary>
    public string QrSvg { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the address only works on the home network, such as nas.lan.</summary>
    public bool HomeOnly { get; set; }
}
