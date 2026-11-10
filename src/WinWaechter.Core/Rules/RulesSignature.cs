using System.Security.Cryptography;
using System.Text;

namespace WinWaechter.Core.Rules;

// Rule files are signed with the update key, but over a domain prefix + the exact file bytes,
// so a release signature can never pass as a rules signature (and vice versa).
public static class RulesSignature
{
    public const string Domain = "winwaechter-rules\n";

    public static byte[] Payload(byte[] json)
    {
        var prefix = Encoding.UTF8.GetBytes(Domain);
        var all = new byte[prefix.Length + json.Length];
        prefix.CopyTo(all, 0);
        json.CopyTo(all, prefix.Length);
        return all;
    }

    // ECDSA P-256/SHA-256, IeeeP1363 (64 bytes), Base64. False on any error.
    public static bool Verify(byte[] json, string sigBase64, string publicKeyPem)
    {
        try
        {
            var sig = Convert.FromBase64String(sigBase64.Trim());
            if (sig.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256) return false;
            return key.VerifyData(Payload(json), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
