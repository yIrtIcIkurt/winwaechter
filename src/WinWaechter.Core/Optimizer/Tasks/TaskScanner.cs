using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Tasks;

/// <summary>Lists enabled third-party scheduled tasks that could be disabled.</summary>
public sealed class TaskScanner
{
    readonly ITaskStore _store;
    readonly ISignatureVerifier _sig;
    readonly RuleSet _rules;

    public TaskScanner(ITaskStore store, ISignatureVerifier sig, RuleSet rules)
    {
        _store = store; _sig = sig; _rules = rules;
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var t in _store.List())
        {
            if (!t.Enabled || t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;

            var location = $"{t.ActionExe} {t.ActionArgs}".Trim();
            var exe = string.IsNullOrWhiteSpace(t.ActionExe)
                ? null
                : PathNormalizer.NormalizePath(CommandLineParser.ExtractExePath(t.ActionExe) ?? t.ActionExe);

            var change = new TaskDisableChange(_store, t.Path);
            var id = $"task|{t.Path}";

            if (string.IsNullOrWhiteSpace(exe))
            {
                // No program to judge: only a hidden task is worth showing.
                if (!t.Hidden) continue;
                if (_rules.Classify(new ItemFacts(t.Name, null, t.Author, null)) == RuleVerdict.Protected) continue;
                result.Add(new Finding(id, FindingArea.Aufgabe, t.Name, "(keine Programmaktion)",
                    ["versteckt", "keine Programmaktion"], Rating.Info, false, change));
                continue;
            }

            if (IsUnder(exe, windows)) continue;

            SignatureInfo sig;
            try { sig = _sig.Verify(exe); } catch { sig = new SignatureInfo(SignatureState.Unknown, null, false); }
            if (sig.State == SignatureState.Valid && sig.IsMicrosoft) continue;

            var verdict = _rules.Classify(new ItemFacts(t.Name, exe, sig.Publisher, location));
            if (verdict == RuleVerdict.Protected) continue;

            var unsigned = sig.State is SignatureState.Unsigned or SignatureState.Invalid;
            var oddPlace = IsAppDataOrTemp(exe);
            var reasons = new List<string>();
            if (t.Hidden) reasons.Add("versteckt");
            if (unsigned) reasons.Add("Ziel unsigniert");
            else if (sig.State == SignatureState.Unknown) reasons.Add("Signatur nicht prüfbar");
            else reasons.Add($"nicht von Microsoft signiert ({sig.Publisher ?? "Herausgeber unbekannt"})");
            if (oddPlace) reasons.Add("Ziel in AppData/Temp");

            Rating rating;
            bool preselect;
            if (t.Hidden && unsigned && oddPlace)
            {
                // The Cua-Driver case: hidden task starting an unsigned file from AppData/Temp.
                rating = Rating.Verdaechtig; preselect = true;
            }
            else if (sig.State == SignatureState.Unknown)
            {
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
                        rating = Rating.Entbehrlich; preselect = true; reasons.Add("Entbehrliche Aufgabe laut Regelliste"); break;
                    case RuleVerdict.Pua:
                        rating = Rating.Unerwuenscht; preselect = true; reasons.Add("Unerwünschte Software laut Regelliste"); break;
                    case RuleVerdict.Miner:
                        rating = Rating.Verdaechtig; preselect = false; reasons.Add("Entspricht einem Muster für Kryptominer"); break;
                    default:
                        rating = Rating.Info; preselect = false; reasons.Add("Unbekannte Aufgabe, nicht in der Regelliste"); break;
                }
            }

            result.Add(new Finding(id, FindingArea.Aufgabe, t.Name, location, reasons, rating, preselect, change,
                Publisher: sig.Publisher));
        }
        return result;
    }

    static bool IsUnder(string path, string dir) =>
        !string.IsNullOrEmpty(dir) && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static bool IsAppDataOrTemp(string path) =>
        path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
        || IsUnder(path, Path.GetTempPath())
        || IsUnder(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
}
