using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.Proxy.Settings;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// What the dashboard shows about the proxy's certificate. Never carries key material.
/// </summary>
public sealed class CertificateStatus
{
    /// <summary>Gets or sets the configured source.</summary>
    public CertificateMode Mode { get; set; }

    /// <summary>Gets or sets a value indicating whether a usable certificate is loaded.</summary>
    public bool Loaded { get; set; }

    /// <summary>Gets or sets the certificate subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the certificate issuer.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets when the certificate expires (UTC).</summary>
    public DateTime? NotAfterUtc { get; set; }

    /// <summary>Gets or sets when the certificate became valid (UTC).</summary>
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>Gets or sets the DNS names the certificate covers.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>Gets or sets configured hostnames the certificate does not cover.</summary>
    public List<string> UncoveredHostnames { get; set; } = new();

    /// <summary>Gets or sets the last load error, empty when none.</summary>
    public string Error { get; set; } = string.Empty;
}
