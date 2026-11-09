using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Offers top-level program folders that no installed program claims any more (never preselected, Info).
/// Deliberately conservative: when in doubt a folder is kept, and nothing is offered at all when
/// no uninstall entry can be read or the process list is unavailable.
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
    readonly IReadOnlyList<string> _roots;

    public LeftoverFolderScanner(IFileOps f, IRegistry reg, RuleSet rules, IProcessList procs, IReadOnlyList<string> roots)
    {
        _files = f; _reg = reg; _rules = rules; _procs = procs; _roots = roots;
    }

    /// <summary>Program Files (64/32 bit) and the per-user Programs folder; AppData and ProgramData are never scanned.</summary>
    public static IReadOnlyList<string> DefaultRoots(CleanerPaths p) =>
        new[] { p.ProgramFiles, p.ProgramFilesX86, Path.Combine(p.LocalAppData, "Programs") }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        var index = InstalledProgramIndex.Load(_reg);
        if (index.UninstallEntryCount == 0) return result;

        IReadOnlyList<RunningProcess> running;
        try { running = _procs.Snapshot(); }
        catch (Exception) { return result; }   // cannot tell what runs: offer nothing

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
                    if (running.Any(r => r.ExePath is not null && r.ExePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))) continue;
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

    IReadOnlyList<DirectoryInfo> SafeDirs(string root)
    {
        try { return string.IsNullOrWhiteSpace(root) ? [] : _files.EnumerateTopLevelDirs(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
