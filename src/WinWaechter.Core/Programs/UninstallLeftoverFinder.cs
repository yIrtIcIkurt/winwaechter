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
                .Concat(ProgramTraces.ByName(scanned, removed.DisplayName, _files))
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

    // Existing, fully readable, no reparse points and nothing running from it (an unreadable process
    // list counts as running); otherwise no finding.
    Finding? FolderFinding(InstalledProgram removed, string folder)
    {
        if (ProgramFolderRules.Inspect(_files, folder) is not { State: FolderState.Ready, Facts: { } facts }) return null;
        if (ProgramFolderRules.RunsFromFolder(_procs, folder) != false) return null;
        return new Finding($"leftover|{folder}", FindingArea.Speicher, Path.GetFileName(folder), folder,
            [$"Rest von „{removed.DisplayName}“"], Rating.Info, false, MoveToAblageChange.ForFolder(_files, folder),
            facts.Bytes, Group: FindingGroups.OrphanFolders);
    }
}
