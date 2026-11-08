using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Autostart;

/// <summary>Lists Run-key and Startup-folder autostarts with a rating and an optional disable change.</summary>
public sealed class AutostartScanner
{
    const string Cv = @"Software\Microsoft\Windows\CurrentVersion";
    const string ApprovedRoot = Cv + @"\Explorer\StartupApproved\";

    readonly IRegistry _reg;
    readonly IUserContext _user;
    readonly ISignatureVerifier _sig;
    readonly RuleSet _rules;
    readonly IFileOps _files;
    readonly IShortcutResolver _lnk;

    public AutostartScanner(IRegistry r, IUserContext u, ISignatureVerifier s, RuleSet rules, IFileOps f, IShortcutResolver lnk)
    {
        _reg = r; _user = u; _sig = s; _rules = rules; _files = f; _lnk = lnk;
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        ScanRunKey(result, Hive.CurrentUser, Cv + @"\Run", "Run");
        ScanRunKey(result, Hive.LocalMachine, Cv + @"\Run", "Run");
        ScanRunKey(result, Hive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");
        ScanFolder(result, Hive.CurrentUser, _user.StartupDir);
        ScanFolder(result, Hive.LocalMachine, _user.CommonStartupDir);
        return result;
    }

    void ScanRunKey(List<Finding> result, Hive hive, string runKey, string approvedSub)
    {
        foreach (var name in _reg.ValueNames(hive, runKey))
        {
            if (_reg.Get(hive, runKey, name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            Add(result, hive, approvedSub, name, name, cmd, CommandLineParser.ExtractExePath(cmd));
        }
    }

    void ScanFolder(List<Finding> result, Hive hive, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var f in _files.EnumerateOlderThan(dir, TimeSpan.Zero))
        {
            if (!SameDir(f.DirectoryName, dir)) continue;                       // top level only
            if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? _lnk.ResolveTarget(f.FullName) : f.FullName;
            Add(result, hive, "StartupFolder", f.Name, Path.GetFileNameWithoutExtension(f.Name), f.FullName, exe);
        }
    }

    // location doubles as the command line handed to the rules.
    void Add(List<Finding> result, Hive hive, string approvedSub, string valueName, string title,
        string location, string? exe)
    {
        SignatureInfo? sig = null;
        if (!string.IsNullOrWhiteSpace(exe))
            try { sig = _sig.Verify(exe); } catch { sig = null; }

        var verdict = _rules.Classify(new ItemFacts(title, exe, sig?.Publisher, location));
        if (verdict == RuleVerdict.Protected) return;

        var approvedKey = ApprovedRoot + approvedSub;
        var status = ReadStatus(hive, approvedKey, valueName);
        var id = $"autostart|{(hive == Hive.CurrentUser ? "HKCU" : "HKLM")}|{approvedSub}|{valueName}";

        var reasons = new List<string>
        {
            sig is not null ? SignatureReason(sig) : "Signatur: Programmdatei nicht ermittelbar",
        };

        Rating rating;
        bool preselect;
        switch (verdict)
        {
            case RuleVerdict.Expendable:
                rating = Rating.Entbehrlich; preselect = true; reasons.Add("Entbehrlicher Autostart laut Regelliste"); break;
            case RuleVerdict.Pua:
                rating = Rating.Unerwuenscht; preselect = true; reasons.Add("Unerwünschte Software laut Regelliste"); break;
            case RuleVerdict.Miner:
                rating = Rating.Verdaechtig; preselect = false; reasons.Add("Entspricht einem Muster für Kryptominer"); break;
            default:
                rating = Rating.Info; preselect = false; reasons.Add("Unbekanntes Programm, nicht in der Regelliste"); break;
        }

        StartupApprovedChange? change = new(_reg, hive, approvedKey, valueName);
        if (status == ApprovedStatus.Disabled)
        {
            rating = Rating.Info; preselect = false; change = null;
            reasons.Add("Bereits deaktiviert");
        }
        else if (status == ApprovedStatus.Unclear)
        {
            preselect = false;
            reasons.Add("Status unklar");
        }

        result.Add(new Finding(id, FindingArea.Autostart, title, location, reasons, rating, preselect, change,
            Publisher: sig?.Publisher));
    }

    enum ApprovedStatus { Enabled, Disabled, Unclear }

    ApprovedStatus ReadStatus(Hive hive, string approvedKey, string valueName)
    {
        if (_reg.Get(hive, approvedKey, valueName) is not byte[] b) return ApprovedStatus.Enabled;
        if (b.Length == 0) return ApprovedStatus.Unclear;
        return b[0] switch
        {
            0x02 or 0x06 => ApprovedStatus.Enabled,
            0x03 or 0x07 => ApprovedStatus.Disabled,
            _ => ApprovedStatus.Unclear,
        };
    }

    static string SignatureReason(SignatureInfo s) => s.State switch
    {
        SignatureState.Valid => $"Signatur: gültig, Herausgeber {s.Publisher ?? "unbekannt"}",
        SignatureState.Unsigned => "Signatur: nicht signiert",
        SignatureState.Invalid => "Signatur: ungültig",
        _ => "Signatur: nicht prüfbar",
    };

    static bool SameDir(string? a, string b) =>
        string.Equals(a?.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
