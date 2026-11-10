using System.Security.Cryptography;
using System.Text;

namespace WinWaechter.Core.Rules;

// Rule files are signed with the update key, but over a domain prefix + the exact file bytes,
// so a release signature can never pass as a rules signature (and vice versa).
public static class RulesSignature
{
    public const string Domain = "winwaechter-rules\n";

    /// <summary>OID of NIST P-256 (secp256r1); other 256-bit curves are refused.</summary>
    public const string P256Oid = "1.2.840.10045.3.1.7";

    public static byte[] Payload(byte[] json)
    {
        var prefix = Encoding.UTF8.GetBytes(Domain);
        var all = new byte[prefix.Length + json.Length];
        prefix.CopyTo(all, 0);
        json.CopyTo(all, prefix.Length);
        return all;
    }

    // ECDSA P-256/SHA-256, IeeeP1363 (64 bytes), Base64. False on any error or a missing signature.
    public static bool Verify(byte[] json, string? sigBase64, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(sigBase64)) return false;
        try
        {
            var sig = Convert.FromBase64String(sigBase64.Trim());
            if (sig.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256 || !IsP256(key)) return false;
            return key.VerifyData(Payload(json), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    // The OID may come back only as a friendly name (CNG), so resolve it before comparing.
    static bool IsP256(ECDsa key)
    {
        var curve = key.ExportParameters(false).Curve;
        if (!curve.IsNamed) return false;
        var oid = curve.Oid.Value;
        if (string.IsNullOrEmpty(oid) && !string.IsNullOrEmpty(curve.Oid.FriendlyName))
            oid = Oid.FromFriendlyName(curve.Oid.FriendlyName, OidGroup.All).Value;
        return oid == P256Oid;
    }
}
