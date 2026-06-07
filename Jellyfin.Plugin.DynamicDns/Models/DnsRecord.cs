using System;

namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// A single DNS record kept in sync with the server's current public IP.
/// Credentials are generic (login/password/zone/server) to cover ddclient's whole protocol set;
/// each provider's documentation describes what those fields mean for it.
/// </summary>
public class DnsRecord
{
    /// <summary>Gets or sets the stable identifier used to key the record in the UI.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets a friendly label shown in the configuration page.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider/protocol this record is updated against.</summary>
    public DnsProviderKind Provider { get; set; } = DnsProviderKind.Cloudflare;

    /// <summary>Gets or sets the fully-qualified hostname to update (e.g. <c>home.example.com</c>).</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the record is updated. Disabled records are skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the A (IPv4) record is maintained.</summary>
    public bool UpdateIPv4 { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the AAAA (IPv6) record is maintained.</summary>
    public bool UpdateIPv6 { get; set; }

    // --- Generic credentials (meaning is provider-specific) ---

    /// <summary>Gets or sets the login / username / API key / email (provider-specific).</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>Gets or sets the password / API token / secret (provider-specific).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the DNS zone name or zone ID, for providers that need it.</summary>
    public string Zone { get; set; } = string.Empty;

    /// <summary>Gets or sets an override update/API server host. Empty uses the provider default.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Gets or sets the record TTL in seconds (provider-specific; <c>1</c> often means automatic).</summary>
    public int Ttl { get; set; } = 1;

    // --- Extras used by a minority of providers ---

    /// <summary>Gets or sets a value indicating whether the Cloudflare record is proxied (orange cloud).</summary>
    public bool Proxied { get; set; }

    /// <summary>Gets or sets a value indicating whether wildcard updates are enabled (dyndns family).</summary>
    public bool Wildcard { get; set; }

    /// <summary>Gets or sets the MX host to set (dyndns family). Empty disables.</summary>
    public string Mx { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the MX is a backup MX (dyndns family).</summary>
    public bool BackupMx { get; set; }

    /// <summary>Gets or sets a value indicating whether the static DNS service is used (dyndns1/dslreports).</summary>
    public bool Static { get; set; }

    // --- Status (written by the updater, surfaced read-only in the UI) ---

    /// <summary>Gets or sets the last IPv4 address pushed for this record.</summary>
    public string LastIPv4 { get; set; } = string.Empty;

    /// <summary>Gets or sets the last IPv6 address pushed for this record.</summary>
    public string LastIPv6 { get; set; } = string.Empty;

    /// <summary>Gets or sets the message from the most recent update attempt.</summary>
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the outcome of the most recent run: <c>Updated</c>, <c>Unchanged</c>,
    /// <c>No address</c>, or <c>Failed</c>. Lets the dashboard distinguish a real push from a
    /// skipped (already-current) check. Empty for records last touched before this was added.
    /// </summary>
    public string LastAction { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the most recent attempt succeeded.</summary>
    public bool LastSuccess { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the most recent update attempt.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>
    /// Returns a shallow copy with the supplied (decrypted) credentials. Used transiently when handing a
    /// record to a provider, so the decrypted secrets never replace the encrypted values held in config.
    /// </summary>
    /// <param name="login">The decrypted login.</param>
    /// <param name="password">The decrypted password.</param>
    /// <returns>A transient copy carrying the decrypted credentials.</returns>
    public DnsRecord WithSecrets(string login, string password)
    {
        var copy = (DnsRecord)MemberwiseClone();
        copy.Login = login;
        copy.Password = password;
        return copy;
    }
}
