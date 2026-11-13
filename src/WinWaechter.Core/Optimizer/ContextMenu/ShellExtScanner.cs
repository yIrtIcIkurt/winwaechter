using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.ContextMenu;

/// <summary>
/// Lists third-party Explorer context-menu entries (shell extension handlers and static verbs) that can be
/// switched off with Windows' own mechanisms. Fails closed: whatever cannot be attributed to a fully qualified
/// program outside Windows is not listed. Static verbs are one finding per verb key (the change switches off
/// exactly that key); handlers are one finding per CLSID with all its places merged.
/// </summary>
public sealed class ShellExtScanner(IRegistry reg, ISignatureVerifier sig, RuleSet rules, string windowsDir, string ownDir)
{
    static readonly HashSet<string> WindowsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "edit", "print", "printto", "runas", "find", "explore", "cmd", "powershell", "pintohome", "Share",
    };

    sealed class HandlerHit(string name, Hive hive)
    {
        public string Name { get; } = name;
        public Hive Hive { get; } = hive;
        public List<string> Labels { get; } = [];
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        ScanHandlers(result);
        ScanVerbs(result);
        return result;
    }

    void ScanHandlers(List<Finding> result)
    {
        var blocked = BlockedClsids();
        var hits = new Dictionary<Guid, HandlerHit>();
        foreach (var (hive, root) in ShellExtLocations.ClassRoots)
            foreach (var (loc, label) in ShellExtLocations.All)
            {
                try
                {
                    var handlers = $"{root}\\{loc}\\shellex\\ContextMenuHandlers";
                    foreach (var name in reg.SubKeyNames(hive, handlers))
                    {
                        if (ClsidOf(hive, $"{handlers}\\{name}", name) is not { } id) continue;
                        if (!hits.TryGetValue(id, out var hit)) hits[id] = hit = new HandlerHit(name, hive);
                        if (!hit.Labels.Contains(label)) hit.Labels.Add(label);
                    }
                }
                catch (Exception)
                {
                    // unreadable place: skip it
                }
            }

        foreach (var (clsid, hit) in hits)
        {
            if (blocked.Contains(clsid)) continue;
            try { AddHandler(result, clsid, hit); }
            catch (Exception)
            {
                // fail closed
            }
        }
    }

    void AddHandler(List<Finding> result, Guid clsid, HandlerHit hit)
    {
        var dll = ServerPath(clsid);
        if (dll is null) return;
        var named = ReadString(hit.Hive, $"{ClassRoot(hit.Hive)}\\CLSID\\{clsid:B}", "")
                    ?? ReadString(Other(hit.Hive), $"{ClassRoot(Other(hit.Hive))}\\CLSID\\{clsid:B}", "");
        var handlerName = Guid.TryParse(hit.Name, out _) ? null : hit.Name;
        var title = FirstText(named, handlerName, Path.GetFileNameWithoutExtension(dll), clsid.ToString("B"));
        if (Judge(dll, title, handlerName, null) is not { } j) return;

        result.Add(Make($"shellext|{clsid:B}", title, hit.Labels, j, ShellExtChange.Block(reg, clsid, title)));
    }

    void ScanVerbs(List<Finding> result)
    {
        foreach (var (hive, root) in ShellExtLocations.ClassRoots)
            foreach (var (loc, label) in ShellExtLocations.All)
            {
                try
                {
                    var shell = $"{root}\\{loc}\\shell";
                    foreach (var verb in reg.SubKeyNames(hive, shell))
                    {
                        if (!ShellExtChange.IsVerbName(verb) || WindowsVerbs.Contains(verb)) continue;
                        try { AddVerb(result, hive, loc, label, $"{shell}\\{verb}", verb); }
                        catch (Exception)
                        {
                            // fail closed
                        }
                    }
                }
                catch (Exception)
                {
                    // unreadable place: skip it
                }
            }
    }

    void AddVerb(List<Finding> result, Hive hive, string loc, string label, string verbKey, string verb)
    {
        if (reg.GetKind(hive, verbKey, ShellExtLocations.LegacyDisableValue) is not null
            || reg.GetKind(hive, verbKey, "ProgrammaticAccessOnly") is not null) return;
        var command = ReadString(hive, verbKey + "\\command", "");
        if (command is null) return;
        var exe = CommandLineParser.ExtractExePath(command);
        if (exe is null) return;

        var shown = ReadString(hive, verbKey, "MUIVerb") ?? ReadString(hive, verbKey, "");
        var title = FirstText(shown is not null && !shown.StartsWith('@') ? shown.Replace("&", "") : null, verb);
        if (Judge(exe, title, verb, command) is not { } j) return;

        result.Add(Make($"shellext|{RegDword.HiveName(hive)}|{loc}|{verb}", title, [label], j,
            ShellExtChange.LegacyDisable(reg, hive, verbKey, title)));
    }

    // Null when the entry must not be offered; otherwise the normalized program path and its signature.
    (string Path, SignatureInfo? Sig)? Judge(string program, string title, string? altName, string? commandLine)
    {
        var path = FullPath(program);
        if (path is null) return null;
        var win = FullPath(windowsDir);
        if (win is null || IsUnder(path, win)) return null;
        var own = FullPath(ownDir);
        if (own is not null && IsUnder(path, own)) return null;

        SignatureInfo? s;
        try { s = sig.Verify(path); }
        catch (Exception) { s = null; }
        if (s is { IsMicrosoft: true }) return null;

        var cmd = commandLine ?? path;
        if (rules.Classify(new ItemFacts(title, path, s?.Publisher, cmd)) == RuleVerdict.Protected) return null;
        if (altName is not null && !string.Equals(altName, title, StringComparison.Ordinal)
            && rules.Classify(new ItemFacts(altName, path, s?.Publisher, cmd)) == RuleVerdict.Protected) return null;
        return (path, s);
    }

    static Finding Make(string id, string title, IReadOnlyList<string> labels, (string Path, SignatureInfo? Sig) j, ShellExtChange change)
    {
        var place = string.Join(", ", labels);
        var line = j.Sig is not null ? SignatureText.Line(j.Sig) : "Signatur: Programmdatei nicht ermittelbar";
        return new Finding(id, FindingArea.Einstellung, title, place, [line, $"Ort: {place}"], Rating.Info,
            Preselected: false, change, Publisher: j.Sig?.Publisher, Group: FindingGroups.ContextMenu, ExePath: j.Path);
    }

    HashSet<Guid> BlockedClsids()
    {
        var set = new HashSet<Guid>();
        try
        {
            foreach (var n in reg.ValueNames(Hive.LocalMachine, ShellExtLocations.BlockedKey))
                if (Guid.TryParse(n, out var g)) set.Add(g);
        }
        catch (Exception)
        {
            // nothing blocked that we can see
        }
        return set;
    }

    Guid? ClsidOf(Hive hive, string handlerKey, string name)
    {
        if (Guid.TryParse(name, out var g)) return g;
        return ReadString(hive, handlerKey, "") is { } v && Guid.TryParse(v.Trim(), out var g2) ? g2 : null;
    }

    // The DLL behind a CLSID (machine registration first); null when there is none.
    string? ServerPath(Guid clsid)
    {
        foreach (var (hive, root) in ShellExtLocations.ClassRoots)
            if (ReadString(hive, $"{root}\\CLSID\\{clsid:B}\\InprocServer32", "") is { } p)
                return Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
        return null;
    }

    string? ReadString(Hive hive, string key, string name) =>
        reg.Get(hive, key, name) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    static string ClassRoot(Hive h) => ShellExtLocations.ClassRoots.First(r => r.Hive == h).Root;
    static Hive Other(Hive h) => h == Hive.LocalMachine ? Hive.CurrentUser : Hive.LocalMachine;

    static string FirstText(params string?[] candidates) =>
        candidates.First(c => !string.IsNullOrWhiteSpace(c))!.Trim();

    static string? FullPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            var t = p.Trim().Trim('"');
            return Path.IsPathFullyQualified(t) ? Path.GetFullPath(t).TrimEnd('\\', '/') : null;
        }
        catch (Exception) { return null; }
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
