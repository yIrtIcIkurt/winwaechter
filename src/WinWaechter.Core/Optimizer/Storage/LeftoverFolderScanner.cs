using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Offers top-level program folders that no installed program claims any more (never preselected, Info).
/// Deliberately conservative: when in doubt a folder is kept, and nothing is offered at all when any
/// source (uninstall entries, Appx list, processes, services, tasks, Run keys) cannot be read completely
/// or the native HKLM Uninstall key yields no entry.
/// </summary>
public sealed class LeftoverFolderScanner
{
    public static readonly TimeSpan MinQuiet = TimeSpan.FromDays(30);
    public const string GroupHint = "Vor dem Verschieben prüfen – manche Programme hinterlassen absichtlich Daten (z. B. Spielstände, Lizenzen).";
    const string NoProgram = "Kein installiertes Programm gefunden";

    readonly IFileOps _files;
    readonly IRegistry _reg;
    readonly RuleSet _rules;
    readonly IProcessList _procs;
    readonly IServiceControl _services;
    readonly ITaskStore _tasks;
    readonly IReadOnlyList<string> _roots;

    static readonly (Hive Hive, string Key)[] RunKeys =
    [
        (Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Hive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Hive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Hive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        (Hive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public LeftoverFolderScanner(IFileOps f, IRegistry reg, RuleSet rules, IProcessList procs, IServiceControl services, ITaskStore tasks, IReadOnlyList<string> roots)
    {
        _files = f; _reg = reg; _rules = rules; _procs = procs; _services = services; _tasks = tasks; _roots = roots;
    }

    /// <summary>Program Files (64/32 bit) and the per-user Programs folder; AppData and ProgramData are never scanned.</summary>
    public static IReadOnlyList<string> DefaultRoots(CleanerPaths p) =>
        new[] { p.ProgramFiles, p.ProgramFilesX86,
                string.IsNullOrWhiteSpace(p.LocalAppData) ? null : Path.Combine(p.LocalAppData, "Programs") }
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        var index = InstalledProgramIndex.Load(_reg);
        // an incomplete index would make installed programs look orphaned
        if (index.ReadFailed || index.MachineEntryCount == 0) return result;

        // paths of running programs, services, tasks and autostarts; unreadable: offer nothing
        if (UsedPaths() is not { } used) return result;

        var cutoff = DateTime.UtcNow - MinQuiet;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            foreach (var d in SafeDirs(root))
            {
                try
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var name = d.Name;
                    var dir = d.FullName.TrimEnd('\\');
                    if (!seen.Add(dir)) continue;
                    if (_rules.IsLeftoverIgnored(name)) continue;
                    if (_rules.Classify(new ItemFacts(name, dir + "\\", null, null)) == RuleVerdict.Protected) continue;
                    if (index.Matches(name)) continue;
                    var inside = dir + "\\";
                    if (used.Any(u => u.Contains(inside, StringComparison.OrdinalIgnoreCase))) continue;
                    if (_files.InspectFolder(dir) is not { } facts) continue;
                    if (facts.HasReparsePoints || !facts.HasBinaries || facts.LatestWriteUtc >= cutoff) continue;

                    result.Add(new Finding($"leftover|{dir}", FindingArea.Speicher, name, dir,
                        [NoProgram, $"Zuletzt geändert am {facts.LatestWriteUtc.ToLocalTime():dd.MM.yyyy}"],
                        Rating.Info, false, MoveToAblageChange.ForFolder(_files, dir), facts.Bytes,
                        Group: FindingGroups.OrphanFolders));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // vanished or unreadable meanwhile: skip it
                }
            }
        }
        return result;
    }

    // Raw and normalized command lines / paths; a folder is kept when any of them contains "<dir>\".
    List<string>? UsedPaths()
    {
        var raw = new List<string?>();
        try
        {
            raw.AddRange(_procs.Snapshot().Select(r => r.ExePath));
            foreach (var s in _services.List()) { raw.Add(s.ImagePath); raw.Add(s.ServiceDll); }
            foreach (var t in _tasks.List()) { raw.Add(t.ActionExe); raw.Add(t.ActionArgs); }
            foreach (var (h, key) in RunKeys)
                foreach (var name in _reg.ValueNames(h, key))
                    raw.Add(_reg.Get(h, key, name) as string);
        }
        catch (Exception) { return null; }

        var used = new List<string>();
        foreach (var r in raw.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            used.Add(r!);
            used.Add(PathNormalizer.NormalizePath(r!));
            used.Add(Environment.ExpandEnvironmentVariables(r!));
        }
        return used;
    }

    IReadOnlyList<DirectoryInfo> SafeDirs(string root)
    {
        try { return string.IsNullOrWhiteSpace(root) ? [] : _files.EnumerateTopLevelDirs(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
