namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// A hostname a DDNS record keeps pointed at this server, offered as a choice for the proxy's hostnames.
/// </summary>
/// <param name="Hostname">The normalized hostname or wildcard.</param>
/// <param name="Record">The record's friendly name, or its hostname when unnamed.</param>
/// <param name="Enabled">Whether the record is enabled.</param>
public sealed record DdnsHostname(string Hostname, string Record, bool Enabled);
