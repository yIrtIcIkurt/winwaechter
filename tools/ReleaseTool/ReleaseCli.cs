using WinWaechter.Core.Update;

namespace WinWaechter.ReleaseTool;

// Commands: keygen <dir> | sign <keyPem> <version> <setupExe> <outDir> | verify <pubPem> <installed> <sums> <sig> <setup>
// | verify-embedded <installed> <sums> <sig> <setup> (checks against the key compiled into the app)
// Exit code 0 = success, 1 = failure (message on stderr). Never prints key material.
public static class ReleaseCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "keygen" when args.Length == 2:
                    var (priv, pub) = ReleaseSigner.GenerateKeyPair(args[1]);
                    output.WriteLine($"Privater Schlüssel: {priv}");
                    output.WriteLine($"Öffentlicher Schlüssel: {pub}");
                    return 0;

                case "sign" when args.Length == 5:
                    if (!SemVer.TryParse(args[2], out var v)) return Fail(error, "Ungültige Version.");
                    var (sums, sig) = ReleaseSigner.Sign(args[1], v, args[3], args[4]);
                    output.WriteLine($"Geschrieben: {sums}");
                    output.WriteLine($"Geschrieben: {sig}");
                    return 0;

                case "verify" when args.Length == 6:
                    return Verify(File.ReadAllText(args[1]), args[2..], output, error);

                case "verify-embedded" when args.Length == 5:
                    return Verify(UpdateKeys.PublicKeyPem, args[1..], output, error);

                default:
                    return Fail(error, "Verwendung:\n  ReleaseTool keygen <dir>\n  ReleaseTool sign <keyPem> <version> <setupExe> <outDir>\n"
                        + "  ReleaseTool verify <pubPem> <installedVersion> <sumsPath> <sigPath> <setupPath>\n"
                        + "  ReleaseTool verify-embedded <installedVersion> <sumsPath> <sigPath> <setupPath>");
            }
        }
        catch (Exception e)
        {
            return Fail(error, e.Message);
        }
    }

    // a = installed, sums, sig, setup
    static int Verify(string pem, string[] a, TextWriter output, TextWriter error)
    {
        if (!SemVer.TryParse(a[0], out var installed)) return Fail(error, "Ungültige installierte Version.");
        // The version is read from the (still unverified) file only to build the info; Verify checks the signature first.
        if (!SumsFile.TryParse(File.ReadAllBytes(a[1]), out var file) || file is null)
            return Fail(error, "Die Prüfsummen-Datei hat ein ungültiges Format.");
        var name = Path.GetFileName(a[3]);
        var dummy = new Uri("https://github.com/");
        var info = new UpdateInfo(file.Version, "", dummy, dummy, dummy, name);
        using var ok = new ReleaseVerifier(pem, installed).Verify(a[1], a[2], a[3], info);
        output.WriteLine($"OK: Version {ok.Version} ist signiert und die Prüfsumme stimmt.");
        return 0;
    }

    static int Fail(TextWriter error, string msg)
    {
        error.WriteLine(msg);
        return 1;
    }
}
