using System;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// The public part of the self-signed option's root, which devices install to trust the proxy's certificate.
/// </summary>
/// <param name="Pem">The certificate, PEM encoded; no private key.</param>
/// <param name="Fingerprint">Its SHA-256 fingerprint, as devices show it.</param>
/// <param name="NotAfterUtc">When it ends.</param>
public sealed record RootCertificate(string Pem, string Fingerprint, DateTime NotAfterUtc);
