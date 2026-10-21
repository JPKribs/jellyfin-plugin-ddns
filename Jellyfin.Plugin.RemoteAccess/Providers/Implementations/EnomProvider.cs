using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicDns.Providers.Implementations;

/// <summary>
/// eNom (port of ddclient's <c>nic_enom_update</c>). <see cref="DNSRecord.Login"/> holds the base
/// domain and <see cref="DNSRecord.Password"/> holds the domain password. The host label sent as
/// <c>HostName</c> is the hostname relative to the base domain. Each enabled family is set with its
/// own request.
/// </summary>
public sealed class EnomProvider : DNSProviderBase
{
    private const string DefaultServer = "dynamic.name-services.com";

    /// <summary>Initializes a new instance of the <see cref="EnomProvider"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public EnomProvider(IHttpClientFactory httpClientFactory, ILogger<EnomProvider> logger)
        : base(httpClientFactory, logger)
    {
    }

    /// <inheritdoc />
    public override DNSProviderKind Kind => DNSProviderKind.Enom;

    /// <inheritdoc />
    public override string Label => "eNom";

    /// <inheritdoc />
    public override string Hint => "Login is the base domain. Password is the domain password. Hostname is the host.";

    /// <inheritdoc />
    public override ProviderFields Fields => new()
    {
        Hostname = "Host",
        Login = "Base domain",
        Password = "Domain password",
        Server = true,
    };

    /// <inheritdoc />
    public override async Task<DNSUpdateResult> UpdateAsync(DNSRecord record, DetectedIP ip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ip);

        if (string.IsNullOrWhiteSpace(record.Hostname)
            || string.IsNullOrWhiteSpace(record.Login)
            || string.IsNullOrWhiteSpace(record.Password))
        {
            return DNSUpdateResult.Fail("eNom requires a hostname, login (base domain), and domain password.");
        }

        var zone = record.Login.Trim();
        string hostName;
        if (string.Equals(record.Hostname, zone, StringComparison.OrdinalIgnoreCase))
        {
            hostName = "@";
        }
        else if (record.Hostname.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
        {
            hostName = record.Hostname.Substring(0, record.Hostname.Length - zone.Length - 1);
        }
        else
        {
            hostName = record.Hostname;
        }

        var server = ServerBase(record, DefaultServer);

        return await ApplyPerFamilyAsync(
            record,
            ip,
            (type, address, ct) => PushAsync(server, zone, hostName, record.Password, address, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Message)> PushAsync(
        string server,
        string zone,
        string hostName,
        string password,
        string value,
        CancellationToken cancellationToken)
    {
        var url = server + "/interface.asp?Command=SetDNSHost"
            + "&HostName=" + Uri.EscapeDataString(hostName)
            + "&Zone=" + Uri.EscapeDataString(zone)
            + "&DomainPassword=" + Uri.EscapeDataString(password)
            + "&Address=" + Uri.EscapeDataString(value);

        var result = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, "HTTP " + result.Status);
        }

        // eNom signals success with Done=true in the response body.
        return result.Body.Contains("Done=true", StringComparison.OrdinalIgnoreCase)
            ? (true, "set to " + value)
            : (false, "failed (" + FirstLine(result.Body) + ")");
    }
}
