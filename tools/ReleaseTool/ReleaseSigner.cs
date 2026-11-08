using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WinWaechter.Core.Update;

namespace WinWaechter.ReleaseTool;

// Release-side helpers. Lives in the tool, not in the app. Never logs key material.
public static class ReleaseSigner
{
    public const string PrivateKeyFile = "winwaechter-update.key.pem";
    public const string PublicKeyFile = "winwaechter-update.pub.pem";

    // Writes a new P-256 key pair. Refuses to overwrite either file.
    public static (string privatePath, string publicPath) GenerateKeyPair(string dir)
    {
        Directory.CreateDirectory(dir);
        var priv = Path.Combine(dir, PrivateKeyFile);
        var pub = Path.Combine(dir, PublicKeyFile);
        if (File.Exists(priv) || File.Exists(pub))
            throw new IOException("Schlüsseldateien existieren bereits und werden nicht überschrieben.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            // The private key is created with a current-user-only ACL from the start; failure aborts.
            WriteNew(priv, key.ExportPkcs8PrivateKeyPem(), PrivateOnlyForMe());
            WriteNew(pub, key.ExportSubjectPublicKeyInfoPem(), null);
        }
        catch
        {
            try { File.Delete(priv); } catch { }
            try { File.Delete(pub); } catch { }
            throw;
        }
        return (priv, pub);
    }

    public static (string sumsPath, string sigPath) Sign(string keyPemPath, SemVer version, string setupExe, string outDir)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPemPath));
        return Sign(key, version, setupExe, outDir);
    }

    public static (string sumsPath, string sigPath) Sign(ECDsa key, SemVer version, string setupExe, string outDir)
    {
        var name = Path.GetFileName(setupExe);
        if (name != $"WinWaechter-Setup-{version}.exe")
            throw new ArgumentException($"Dateiname muss WinWaechter-Setup-{version}.exe lauten.");

        byte[] hash;
        using (var fs = File.OpenRead(setupExe)) hash = SHA256.HashData(fs);

        var sums = new UTF8Encoding(false).GetBytes(SumsFile.Build(version, name, hash));
        var sig = key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Directory.CreateDirectory(outDir);
        var sumsPath = Path.Combine(outDir, "SHA256SUMS");
        var sigPath = Path.Combine(outDir, "SHA256SUMS.sig");
        File.WriteAllBytes(sumsPath, sums);
        File.WriteAllText(sigPath, Convert.ToBase64String(sig), Encoding.ASCII);
        return (sumsPath, sigPath);
    }

    static void WriteNew(string path, string text, FileSecurity? acl)
    {
        using var fs = acl is null
            ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            : new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, acl);
        fs.Write(new UTF8Encoding(false).GetBytes(text));
    }

    static FileSecurity PrivateOnlyForMe()
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return sec;
    }
}
