using System.Security.Cryptography;
using System.Text;

namespace Lycoris.Core.Security;

/// <summary>
/// Building blocks for the update channel (the update feature itself lives in Core/Updates). A SHA-256 published in the same
/// release as the file only protects against a damaged download — whoever can replace the file can replace the hash.
/// What protects against a hijacked release is a signature made with a key that is NOT on the release server: the public half
/// is pinned inside the app, the private half stays with the owner. ECDSA P-256 is used because it ships with .NET.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// The owner's release public key (Base64 SubjectPublicKeyInfo, ECDSA P-256). EMPTY until the owner generates a key pair —
    /// see docs/security-audit.md «Update channel requirements». While empty, <see cref="IsPinned"/> is false and an updater must
    /// treat the channel as unauthenticated (hash = integrity only) and say so.
    /// </summary>
    public const string PinnedPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEU9AUI9JPuQR9KmGphVMJ7E8bvwzV2LFFs284DhuQLPhFN4LtbK6qQ7K/4han+3jVMksNIVLYMtpVHoRW2MMzuA==";

    public static bool IsPinned => PinnedPublicKey.Length > 0;

    /// <summary>Makes a key pair for the owner's signing script and for tests. Returns (privateKeyPkcs8Base64, publicKeySpkiBase64).</summary>
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Signs <paramref name="data"/>; returns the 64-byte IEEE P1363 signature as Base64.</summary>
    public static string Sign(byte[] data, string privateKeyPkcs8Base64)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>True only when <paramref name="signatureBase64"/> is a valid signature of <paramref name="data"/> by the given public key. Never throws.</summary>
    public static bool Verify(byte[] data, string? signatureBase64, string? publicKeySpkiBase64)
    {
        if (data.Length == 0 || string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeySpkiBase64)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            if (signature.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64.Trim()), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The text an update signature covers: it binds the file hash to the version AND the file name, so a valid old signature cannot be
    /// re-used for another file or version. Keep the original MONOLITH statement: existing signatures depend on these exact bytes.
    /// </summary>
    public static byte[] SignedStatement(string version, string fileName, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"monolith-release\nversion={version}\nfile={fileName}\nsha256={sha256Hex.ToLowerInvariant()}\n");

    /// <summary>
    /// Rollback protection: a candidate must be strictly newer than the running build and not older than <paramref name="minimumAllowed"/>
    /// (a floor the signed manifest can raise after a vulnerable release). Pure.
    /// </summary>
    public static bool IsAcceptableVersion(Version current, Version candidate, Version? minimumAllowed = null) =>
        candidate > current && (minimumAllowed is null || candidate >= minimumAllowed);
}
