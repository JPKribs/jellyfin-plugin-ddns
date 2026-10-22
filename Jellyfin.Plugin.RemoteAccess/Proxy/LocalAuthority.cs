using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The self-signed option's own certificate authority. A root is made for the proxy's names, valid for three
/// years, and the proxy's certificate is issued from it whenever the names change or it nears expiry. A device
/// that trusts the root, installed once, keeps trusting every certificate issued after, and Apple's devices fully
/// trust it because it is marked as an authority, which a lone self-signed certificate is not.
/// <para>
/// The root carries name constraints: it can only vouch for the proxy's own names, <c>localhost</c> and private
/// addresses. Its key sits on this server, so a copy of it, from a backup for example, must never be able to mint
/// a certificate for any other site that the devices which installed the root would trust.
/// </para>
/// </summary>
internal static class LocalAuthority
{
    /// <summary>How long the root lasts. It is replaced when it is missing, damaged, within a year of ending, or does not cover the names.</summary>
    public static readonly TimeSpan RootLifetime = TimeSpan.FromDays(1095);

    /// <summary>
    /// How long an issued certificate lasts: under the 398 days browsers allow, and well under the 825 Apple
    /// allows for authorities a person installs.
    /// </summary>
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(397);

    private const string NameConstraintsOid = "2.5.29.30";

    private static readonly Asn1Tag _permittedTag = new(TagClass.ContextSpecific, 0);
    private static readonly Asn1Tag _dnsNameTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag _ipAddressTag = new(TagClass.ContextSpecific, 7);

    // The private and loopback ranges the root may vouch for, as address and mask. Every other address is outside
    // them, so the root can never be used for a public address either.
    private static readonly (string Address, string Mask)[] _privateRanges =
    {
        ("10.0.0.0", "255.0.0.0"),
        ("172.16.0.0", "255.240.0.0"),
        ("192.168.0.0", "255.255.0.0"),
        ("127.0.0.0", "255.0.0.0"),
        ("fc00::", "fe00::"),
        ("::1", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")
    };

    /// <summary>
    /// Loads the root from its file, or makes a new one when there is none usable for the names.
    /// </summary>
    /// <param name="path">The root's file, holding its private key.</param>
    /// <param name="names">The DNS names certificates are issued for. The root is limited to them.</param>
    /// <param name="created">Whether a new root was made, which every device then has to install again.</param>
    /// <returns>The root, with its private key.</returns>
    public static X509Certificate2 LoadOrCreateRoot(string path, IReadOnlyCollection<string> names, out bool created)
    {
        ArgumentNullException.ThrowIfNull(names);
        created = false;
        var permitted = ConstraintNames(names);
        if (File.Exists(path))
        {
            try
            {
                // Checked on one copy and handed over as another, so neither is left undisposed if a check throws.
                var saved = File.ReadAllBytes(path);
                bool usable;
                using (var probe = X509CertificateLoader.LoadPkcs12(saved, null))
                {
                    // A root from before name constraints, or one that lasts longer than roots now do, is replaced:
                    // left in place, a copy of its key could vouch for any site.
                    usable = probe.HasPrivateKey
                        && probe.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(365)
                        && probe.NotAfter - probe.NotBefore <= RootLifetime + TimeSpan.FromDays(2)
                        && PermittedDnsNames(probe) is { } allowed
                        && permitted.All(n => Permits(allowed, n));
                }

                if (usable)
                {
                    return X509CertificateLoader.LoadPkcs12(saved, null);
                }
            }
            catch (CryptographicException)
            {
                // A damaged root is replaced: nothing issued from it can be renewed without its key.
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = "CN=Jellyfin Remote Access root (" + Environment.MachineName + "), O=Jellyfin Remote Access";
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints(permitted));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.Add(RootLifetime));
        var bytes = root.Export(X509ContentType.Pkcs12);
        SecretFile.Write(path, bytes);
        created = true;
        return X509CertificateLoader.LoadPkcs12(bytes, null);
    }

    /// <summary>
    /// The DNS names a root made for these names is limited to: each name without a leading <c>*.</c>, in its
    /// ASCII form, and <c>localhost</c>. A limit covers the name and every name under it.
    /// </summary>
    /// <param name="names">The names certificates are issued for.</param>
    /// <returns>The names, lower case and without duplicates.</returns>
    public static IReadOnlyList<string> ConstraintNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var idn = new IdnMapping();
        return names
            .Append("localhost")
            .Select(n => (n ?? string.Empty).Trim().TrimEnd('.'))
            .Select(n => n.StartsWith("*.", StringComparison.Ordinal) ? n[2..] : n)
            .Where(n => n.Length > 0 && !IPAddress.TryParse(n, out _))
            .Select(n =>
            {
                try
                {
                    return idn.GetAscii(n).ToLowerInvariant();
                }
                catch (ArgumentException)
                {
                    return n.ToLowerInvariant();
                }
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The DNS names a root's name constraints permit, or null when it has none.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns>The permitted names, or null for a root that may vouch for anything.</returns>
    public static IReadOnlyList<string>? PermittedDnsNames(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Extensions[NameConstraintsOid] is not { } extension)
        {
            return null;
        }

        try
        {
            var outer = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            if (!outer.HasData || !outer.PeekTag().HasSameClassAndValue(_permittedTag))
            {
                return null;
            }

            var permitted = outer.ReadSequence(_permittedTag);
            var names = new List<string>();
            while (permitted.HasData)
            {
                var subtree = permitted.ReadSequence();
                if (subtree.PeekTag().HasSameClassAndValue(_dnsNameTag))
                {
                    names.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, _dnsNameTag).ToLowerInvariant());
                }
            }

            return names;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    // A name is permitted by a limit it equals or sits under, the way RFC 5280 matches a dNSName constraint.
    private static bool Permits(IReadOnlyList<string> allowed, string name)
        => allowed.Any(a => string.Equals(name, a, StringComparison.Ordinal) || name.EndsWith("." + a, StringComparison.Ordinal));

    // The critical name constraints extension: only these DNS names and the private address ranges.
    private static X509Extension NameConstraints(IReadOnlyList<string> names)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(_permittedTag))
            {
                foreach (var name in names)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, _dnsNameTag);
                    }
                }

                foreach (var (address, mask) in _privateRanges)
                {
                    using (writer.PushSequence())
                    {
                        var bytes = IPAddress.Parse(address).GetAddressBytes().Concat(IPAddress.Parse(mask).GetAddressBytes()).ToArray();
                        writer.WriteOctetString(bytes, _ipAddressTag);
                    }
                }
            }
        }

        return new X509Extension(NameConstraintsOid, writer.Encode(), true);
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
