using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Encrypts the keys that protect saved credentials, where the operating system offers a way to. The shared
/// library writes them as plain XML beside the configuration, so a copy of the data folder, such as a backup,
/// would carry both the encrypted credentials and the key to them.
/// <para>
/// On Windows each key is wrapped with DPAPI for this machine: a copy taken elsewhere cannot be read, and the
/// key still works if Jellyfin later runs under another account. Data Protection reads a wrapped key without
/// being told how, so nothing else changes. Linux and macOS have no such store a service can use; there the
/// folder is kept private instead (<see cref="SecretFile.ProtectFolder"/>).
/// </para>
/// </summary>
internal static class KeyRingProtection
{
    // How Data Protection marks the part of a key that must be encrypted, and how it stores the encrypted form.
    private static readonly XName _requiresEncryption = XName.Get("requiresEncryption", "http://schemas.asp.net/2015/03/dataProtection");
    private static readonly XName _encryptedSecret = XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection");
    private static readonly XName _decryptorType = "decryptorType";

    /// <summary>
    /// Wraps every key in the folder that is still stored in the clear. Safe to run often: a wrapped key is left
    /// alone. Run at each start and daily, since the key ring adds a new plain key every 90 days.
    /// </summary>
    /// <param name="keyDirectory">The key ring folder.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>How many keys were wrapped.</returns>
    public static int Protect(string keyDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsWindows() || !Directory.Exists(keyDirectory))
        {
            return 0;
        }

        var wrapped = 0;
        foreach (var file in Directory.EnumerateFiles(keyDirectory, "key-*.xml"))
        {
            try
            {
                if (WrapFile(file))
                {
                    wrapped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or CryptographicException or InvalidOperationException)
            {
                // The key stays as it was and keeps working; only its protection is missing.
                logger.LogWarning("Could not encrypt the credential key {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
        }

        if (wrapped > 0)
        {
            logger.LogInformation("Encrypted {Count} credential keys with Windows data protection", wrapped);
        }

        return wrapped;
    }

    [SupportedOSPlatform("windows")]
    private static bool WrapFile(string file)
        => WrapFile(file, new DpapiXmlEncryptor(protectToLocalMachine: true, NullLoggerFactory.Instance), new DpapiXmlDecryptor());

    /// <summary>
    /// Wraps one key file's secret parts with the given encryptor, in the form Data Protection reads back: each
    /// becomes <c>encryptedSecret</c> in Data Protection's namespace, naming its decryptor. Exposed for tests,
    /// which use the no-op encryptor where DPAPI is not available.
    /// </summary>
    /// <param name="file">The key file.</param>
    /// <param name="encryptor">Encrypts each secret part.</param>
    /// <param name="decryptor">Reads it back, to check before the file is replaced.</param>
    /// <returns>Whether anything was wrapped.</returns>
    internal static bool WrapFile(string file, IXmlEncryptor encryptor, IXmlDecryptor decryptor)
    {
        var document = XDocument.Load(file);
        var plain = document.Descendants().Where(e => (string?)e.Attribute(_requiresEncryption) == "true").ToList();
        if (plain.Count == 0)
        {
            return false;
        }

        foreach (var element in plain)
        {
            var info = encryptor.Encrypt(new XElement(element));

            // Checked before anything is written: a key that could not be read back would make every saved
            // credential unreadable. Compared as text without layout, since the decryptor drops indentation.
            var back = decryptor.Decrypt(info.EncryptedElement);
            if (!string.Equals(back.ToString(SaveOptions.DisableFormatting), element.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal))
            {
                throw new CryptographicException("The encrypted key did not read back the same.");
            }

            element.ReplaceWith(new XElement(_encryptedSecret, new XAttribute(_decryptorType, info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement));
        }

        using var buffer = new MemoryStream();
        document.Save(buffer, SaveOptions.DisableFormatting);
        SecretFile.Write(file, buffer.ToArray());
        return true;
    }
}
