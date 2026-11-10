using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>
/// Targeted search for what one just-uninstalled program left behind (spec 1.4): its folder and the
/// services, tasks and autostarts that point into it or are orphaned and carry its name. No age rule
/// (the user has just uninstalled it); every other protection rule applies.
/// </summary>
public sealed class UninstallLeftoverFinder
{
    readonly IRegistry _reg;
    readonly IFileOps _files;
    readonly RuleSet _rules;
    readonly IProcessList _procs;
    readonly CleanerPaths _paths;
    readonly Func<IReadOnlyList<Finding>> _traceScan;

    public UninstallLeftoverFinder(IRegistry reg, IFileOps files, RuleSet rules, IProcessList procs, CleanerPaths paths,
        Func<IReadOnlyList<Finding>> traceScan)
    {
        _reg = reg; _files = files; _rules = rules; _procs = procs; _paths = paths; _traceScan = traceScan;
    }

    /// <summary>Empty while the entry still exists (or that cannot be read). Folder finding first, then traces; never throws.</summary>
    public IReadOnlyList<Finding> Find(InstalledProgram removed, IReadOnlyList<InstalledProgram> remaining)
    {
        var result = new List<Finding>();
        try
        {
            if (_reg.KeyExists(removed.Hive, removed.RegistryKey)) return [];

            var raw = string.IsNullOrWhiteSpace(removed.InstallLocation) ? IconFolder(removed.DisplayIcon) : removed.InstallLocation;
            // only an accepted folder is used, also for traces: a refused one may belong to someone else
            var folder = ProgramFolderRules.Check(raw, removed, remaining, _paths, _rules).Folder;
            if (folder is not null && FolderFinding(removed, folder) is { } f) result.Add(f);

            var scanned = _traceScan();
            IReadOnlyList<Finding> inFolder = folder is null ? [] : ProgramTraces.InFolder(scanned, folder);
            var traces = inFolder
                .Concat(ProgramTraces.ByName(scanned, removed.DisplayName))
                .DistinctBy(t => t.Id, StringComparer.Ordinal)
                .Select(t => t with { Preselected = t.Group == FindingGroups.OrphanEntries });
            foreach (var t in traces) result.Add(t);
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Reste-Suche für „{removed.DisplayName}“ abgebrochen", ex);
        }
        return result;
    }

    static string? IconFolder(string? displayIcon) =>
        ProgramInventory.IconExe(displayIcon) is { } exe && Path.IsPathFullyQualified(exe) ? Path.GetDirectoryName(exe) : null;

    // Existing, fully readable, no reparse points and nothing running from it; otherwise no finding.
    Finding? FolderFinding(InstalledProgram removed, string folder)
    {
        if (!Directory.Exists(folder)) return null;
        if (_files.InspectFolder(folder) is not { HasReparsePoints: false } facts) return null;
        if (IsInUse(folder)) return null;
        return new Finding($"leftover|{folder}", FindingArea.Speicher, Path.GetFileName(folder), folder,
            [$"Rest von „{removed.DisplayName}“"], Rating.Info, false, MoveToAblageChange.ForFolder(_files, folder),
            facts.Bytes, Group: FindingGroups.OrphanFolders);
    }

    // An unreadable process list counts as "in use".
    bool IsInUse(string folder)
    {
        IReadOnlyList<RunningProcess> running;
        try { running = _procs.Snapshot(); }
        catch (Exception ex)
        {
            FileLog.Warn("Prozessliste nicht lesbar", ex);
            return true;
        }
        var prefix = folder + "\\";
        foreach (var proc in running)
        {
            if (string.IsNullOrWhiteSpace(proc.ExePath) || !Path.IsPathFullyQualified(proc.ExePath)) continue;
            string full;
            try { full = Path.GetFullPath(proc.ExePath); }
            catch (Exception) { continue; }
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
