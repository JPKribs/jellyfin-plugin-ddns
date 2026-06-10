using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DynamicDns.Models;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Encrypts record credentials in place. Shared by every path that can persist the configuration, so a
/// plaintext secret is caught at the door no matter which endpoint delivered it.
/// </summary>
public static class CredentialEncryption
{
    /// <summary>
    /// Protects the login and password of every record. Values that are already encrypted, or empty,
    /// pass through unchanged.
    /// </summary>
    /// <param name="records">The records to protect.</param>
    /// <param name="secrets">The secret protector.</param>
    /// <returns>Whether any value changed.</returns>
    public static bool ProtectAll(IEnumerable<DNSRecord> records, SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(secrets);

        var changed = false;
        foreach (var record in records)
        {
            var login = secrets.Protect(record.Login);
            if (!string.Equals(login, record.Login, StringComparison.Ordinal))
            {
                record.Login = login;
                changed = true;
            }

            var password = secrets.Protect(record.Password);
            if (!string.Equals(password, record.Password, StringComparison.Ordinal))
            {
                record.Password = password;
                changed = true;
            }
        }

        return changed;
    }
}
