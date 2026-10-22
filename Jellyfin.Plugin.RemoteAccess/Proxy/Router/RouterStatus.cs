using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// What the dashboard shows about the ports the plugin opened on the router.
/// </summary>
public sealed class RouterStatus
{
    /// <summary>Gets or sets a value indicating whether the plugin opens ports on the router on its own.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets how the router was asked: "UPnP", "NAT-PMP", or empty when no router answered.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every port the proxy listens on is forwarded.</summary>
    public bool Mapped { get; set; }

    /// <summary>Gets or sets the external ports currently forwarded to this server.</summary>
    public List<int> Ports { get; set; } = new();

    /// <summary>Gets or sets the address of the device that answered and says it forwards the ports, empty when none did.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that device is this server's default gateway, the usual router.</summary>
    public bool DeviceIsGateway { get; set; }

    /// <summary>Gets or sets the LAN address the ports are forwarded to.</summary>
    public string LanAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the router's own internet address as the router reports it, empty while unknown.</summary>
    public string ExternalAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the provider shares one internet address between customers.</summary>
    public bool BehindCgnat { get; set; }

    /// <summary>Gets or sets why the server looks to be behind a shared address, empty when it does not.</summary>
    public string CgnatReason { get; set; } = string.Empty;

    /// <summary>Gets or sets what went wrong, empty when nothing did.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets when the ports were last opened or renewed (UTC).</summary>
    public DateTime? RenewedUtc { get; set; }

    /// <summary>
    /// Copies the status, so a caller never holds the one the port mapper keeps changing.
    /// </summary>
    /// <returns>The copy.</returns>
    public RouterStatus Copy()
    {
        var copy = (RouterStatus)MemberwiseClone();
        copy.Ports = new List<int>(Ports);
        return copy;
    }
}
