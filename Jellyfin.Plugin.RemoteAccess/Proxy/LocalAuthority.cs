using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The self-signed option's own certificate authority. A root is made once, valid for ten years, and the
/// proxy's certificate is issued from it whenever the names change or it nears expiry. A device that trusts
/// the root, installed once, keeps trusting every certificate issued after, and Apple's devices fully trust it
/// because it is marked as an authority, which a lone self-signed certificate is not.
/// </summary>
internal static class LocalAuthority
{
    /// <summary>How long the root lasts. It is replaced only when it is missing, damaged or within a year of ending.</summary>
    public static readonly TimeSpan RootLifetime = TimeSpan.FromDays(3650);

    /// <summary>
    /// How long an issued certificate lasts: under the 398 days browsers allow, and well under the 825 Apple
    /// allows for authorities a person installs.
    /// </summary>
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(397);

    /// <summary>
    /// Loads the root from its file, or makes a new one when there is none usable.
    /// </summary>
    /// <param name="path">The root's file, holding its private key.</param>
    /// <param name="created">Whether a new root was made, which every device then has to install again.</param>
    /// <returns>The root, with its private key.</returns>
    public static X509Certificate2 LoadOrCreateRoot(string path, out bool created)
    {
        created = false;
        if (File.Exists(path))
        {
            X509Certificate2? existing = null;
            try
            {
                existing = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(365))
                {
                    // Handed to the caller, so not disposed below.
                    var kept = existing;
                    existing = null;
                    return kept;
                }
            }
            catch (CryptographicException)
            {
                // A damaged root is replaced: nothing issued from it can be renewed without its key.
            }
            finally
            {
                existing?.Dispose();
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = "CN=Jellyfin Remote Access root (" + Environment.MachineName + "), O=Jellyfin Remote Access";
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.Add(RootLifetime));
        var bytes = root.Export(X509ContentType.Pkcs12);
        SecretFile.Write(path, bytes);
        created = true;
        return X509CertificateLoader.LoadPkcs12(bytes, null);
    }

    /// <summary>
    /// Issues a certificate for the names from the root.
    /// </summary>
    /// <param name="root">The root, with its private key.</param>
    /// <param name="names">The DNS names to cover.</param>
    /// <returns>The certificate with its private key, as a PKCS#12 file to save and load.</returns>
    public static byte[] Issue(X509Certificate2 root, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(names);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = names.FirstOrDefault(n => !n.StartsWith('*')) ?? "localhost";
        var request = new CertificateRequest("CN=" + first, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        // Never past the root, which would make the certificate fail before its own end.
        var notAfter = DateTimeOffset.UtcNow.Add(LeafLifetime);
        var rootEnd = new DateTimeOffset(root.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), notAfter < rootEnd ? notAfter : rootEnd, RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        return withKey.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    /// Whether a certificate was issued by this root, as a device that trusts the root would check it.
    /// </summary>
    /// <param name="leaf">The certificate.</param>
    /// <param name="root">The root.</param>
    /// <returns>True when the root vouches for it.</returns>
    public static bool IssuedBy(X509Certificate2 leaf, X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(root);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        return chain.Build(leaf) && chain.ChainElements.Count == 2 && chain.ChainElements[1].Certificate.Thumbprint == root.Thumbprint;
    }

    /// <summary>
    /// The root's SHA-256 fingerprint, as devices show it, so a person can check the file they installed.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns>Like <c>3A:5F:…</c>.</returns>
    public static string Fingerprint(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return string.Join(':', SHA256.HashData(root.RawData).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
