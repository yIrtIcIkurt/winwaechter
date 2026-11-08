using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Services;

/// <summary>Lists automatic third-party services outside C:\Windows that could be set to Manual.</summary>
public sealed class ServiceScanner
{
    readonly IServiceControl _sc;
    readonly ISignatureVerifier _sig;
    readonly RuleSet _rules;
    readonly IRegistry _reg;

    // reg is only handed to the changes (service key backup).
    public ServiceScanner(IServiceControl sc, ISignatureVerifier sig, RuleSet rules, IRegistry reg)
    {
        _sc = sc; _sig = sig; _rules = rules; _reg = reg;
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var s in _sc.List())
        {
            if (s.Mode != StartMode.Automatic || string.IsNullOrWhiteSpace(s.ImagePath)) continue;

            var file = FileToCheck(s);
            if (string.IsNullOrWhiteSpace(file) || IsUnder(file, windows)) continue;

            SignatureInfo sig;
            try { sig = _sig.Verify(file); } catch { sig = new SignatureInfo(SignatureState.Unknown, null, false); }
            if (sig.State == SignatureState.Valid && sig.IsMicrosoft) continue;

            var verdict = _rules.Classify(new ItemFacts(s.DisplayName, file, sig.Publisher, s.ImagePath));
            if (verdict == RuleVerdict.Protected) continue;

            var reasons = new List<string> { "Startet automatisch", SignatureReason(sig, file) };
            Rating rating;
            bool preselect;
            if (sig.State == SignatureState.Unknown)
            {
                // Signature could not be checked: never preselect, but show what the rules say.
                rating = Rating.Info; preselect = false;
                if (verdict == RuleVerdict.Expendable) reasons.Add("bekannt als entbehrlich");
                else if (verdict == RuleVerdict.Pua) reasons.Add("bekannt als unerwünscht");
                else if (verdict == RuleVerdict.Miner) reasons.Add("Entspricht einem Muster für Kryptominer");
            }
            else
            {
                switch (verdict)
                {
                    case RuleVerdict.Expendable:
                        rating = Rating.Entbehrlich; preselect = true; reasons.Add("Entbehrlicher Dienst laut Regelliste"); break;
                    case RuleVerdict.Pua:
                        rating = Rating.Unerwuenscht; preselect = true; reasons.Add("Unerwünschte Software laut Regelliste"); break;
                    case RuleVerdict.Miner:
                        rating = Rating.Verdaechtig; preselect = false; reasons.Add("Entspricht einem Muster für Kryptominer"); break;
                    default:
                        rating = Rating.Info; preselect = false; reasons.Add("Unbekannter Dienst, nicht in der Regelliste"); break;
                }
            }

            result.Add(new Finding($"service|{s.Name}", FindingArea.Dienst, s.DisplayName, s.ImagePath, reasons, rating,
                preselect, new ServiceStartModeChange(_sc, _reg, s.Name), Publisher: sig.Publisher));
        }
        return result;
    }

    // svchost-hosted services are judged by their ServiceDll.
    static string? FileToCheck(ServiceInfo s)
    {
        var exe = PathNormalizer.NormalizePath(CommandLineParser.ExtractExePath(s.ImagePath!) ?? "");
        if (!string.IsNullOrWhiteSpace(s.ServiceDll)
            && string.Equals(Path.GetFileName(exe), "svchost.exe", StringComparison.OrdinalIgnoreCase))
            return PathNormalizer.NormalizePath(s.ServiceDll);
        return exe;
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static bool InProgramFiles(string path) =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Any(d => !string.IsNullOrEmpty(d) && IsUnder(path, d));

    static string SignatureReason(SignatureInfo s, string file) => s.State switch
    {
        SignatureState.Valid => $"nicht von Microsoft signiert ({s.Publisher ?? "Herausgeber unbekannt"})",
        SignatureState.Unknown => "Signatur nicht prüfbar",
        _ => InProgramFiles(file) ? "unsigniert" : "unsigniert, ungewöhnlicher Ort",
    };
}
