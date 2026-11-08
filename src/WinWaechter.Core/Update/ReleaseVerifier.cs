using System.Security.Cryptography;
using System.Text;

namespace WinWaechter.Core.Update;

// Order: signature -> strict parse -> version line == info and > installed -> setup name listed -> SHA-256.
// The setup is hashed while held open with FileShare.Read (no writers). Callers must launch the
// installer right after Verify returns; the folder is admin-only (SecureDataDir) to limit swaps.
public sealed class ReleaseVerifier
{
    public const int MaxSmallFileBytes = 64 * 1024;

    readonly string _publicKeyPem;
    readonly SemVer _installed;

    public ReleaseVerifier(string publicKeyPem, SemVer installed)
    {
        _publicKeyPem = publicKeyPem;
        _installed = installed;
    }

    public VerifiedUpdate Verify(string sumsPath, string sigPath, string setupPath, UpdateInfo info)
    {
        var sums = ReadSmall(sumsPath);
        var sigText = Encoding.ASCII.GetString(ReadSmall(sigPath)).Trim();

        // 1. signature
        if (!SignatureValid(sums, sigText))
            throw new UpdateVerificationException("Die Signatur des Updates ist ungültig. Das Update wird nicht installiert.");

        // 2. strict parse + version
        if (!SumsFile.TryParse(sums, out var file) || file is null)
            throw new UpdateVerificationException("Die Prüfsummen-Datei hat ein ungültiges Format.");
        if (file.Version != info.Version)
            throw new UpdateVerificationException("Die signierte Version passt nicht zum Release.");
        if (file.Version <= _installed)
            throw new UpdateVerificationException("Das Update ist nicht neuer als die installierte Version.");

        // 3. file name listed
        if (!SumsFile.IsPlainFileName(info.SetupName) || !file.Hashes.TryGetValue(info.SetupName, out var expected))
            throw new UpdateVerificationException("Die Setup-Datei ist in den signierten Prüfsummen nicht aufgeführt.");

        // 4. SHA-256 of the setup
        byte[] actual;
        try
        {
            using var fs = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            actual = SHA256.HashData(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateVerificationException("Die Setup-Datei konnte nicht gelesen werden.");
        }
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new UpdateVerificationException("Die Prüfsumme der Setup-Datei stimmt nicht überein. Das Update wird nicht installiert.");

        return new VerifiedUpdate(Path.GetFullPath(setupPath), file.Version);
    }

    bool SignatureValid(byte[] data, string sigBase64)
    {
        try
        {
            var sig = Convert.FromBase64String(sigBase64);
            if (sig.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportFromPem(_publicKeyPem);
            if (key.KeySize != 256) return false;
            return key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    static byte[] ReadSmall(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxSmallFileBytes)
                throw new UpdateVerificationException("Die Prüfdatei ist ungewöhnlich groß.");
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            return buf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateVerificationException("Die Prüfdateien konnten nicht gelesen werden.");
        }
    }
}
