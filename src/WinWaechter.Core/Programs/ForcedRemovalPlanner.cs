using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>Every change of a forced removal in apply order, the folder that is moved (or null) and German notes.</summary>
public sealed record ForcedRemovalPlan(InstalledProgram Program, IReadOnlyList<IChange> Changes, string? Folder, IReadOnlyList<string> Notes);

/// <summary>
/// Emergency removal (spec 1.3 / 7.4) of a program whose official uninstaller is missing or left its entry
/// behind: traces in its folder (services to Manual, tasks off, autostarts off), the folder into the
/// Ablage, then the uninstall entry. Everything goes through the engine and can be undone.
/// </summary>
public sealed class ForcedRemovalPlanner
{
    public const string EntryGone = "Der Eintrag existiert nicht mehr.";
    public const string FolderPrefix = "Ordner nicht angeboten: ";
    public const string FolderMissing = "Der Ordner existiert nicht mehr.";
    public const string FolderUnreadable = "Der Ordner ist nicht vollständig lesbar oder enthält Verknüpfungen.";
    public const string FolderInUse = "Ein Programm aus diesem Ordner läuft gerade. Bitte zuerst beenden.";
    public const string ProcessesUnknown = "Laufende Programme konnten nicht geprüft werden.";

    // order in which traces are applied
    static readonly string[] TraceKinds = ["service", "task", "autostart"];

    readonly IRegistry _reg;
    readonly IFileOps _files;
    readonly RuleSet _rules;
    readonly IProcessList _procs;
    readonly CleanerPaths _paths;
    readonly Func<IReadOnlyList<Finding>> _traceScan;

    public ForcedRemovalPlanner(IRegistry reg, IFileOps files, RuleSet rules, IProcessList procs, CleanerPaths paths,
        Func<IReadOnlyList<Finding>> traceScan)
    {
        _reg = reg; _files = files; _rules = rules; _procs = procs; _paths = paths; _traceScan = traceScan;
    }

    /// <summary>Never for protected programs or MSI products; only when the uninstaller is missing or left the entry behind.</summary>
    public static bool IsOffered(InstalledProgram p, bool uninstallerMissing, bool finishedButStillListed) =>
        p.Flag != ProgramFlag.Protected && !p.WindowsInstaller && (uninstallerMissing || finishedButStillListed);

    public ForcedRemovalPlan Plan(InstalledProgram p, IReadOnlyList<InstalledProgram> all)
    {
        if (!_reg.KeyExists(p.Hive, p.RegistryKey)) return new(p, [], null, [EntryGone]);

        var notes = new List<string>();
        var changes = new List<IChange>();
        var check = ProgramFolderRules.Check(p.InstallLocation, p, all, _paths, _rules);
        if (check.Refusal is { } refusal) notes.Add(FolderPrefix + refusal);

        string? moved = null;
        if (check.Folder is { } folder)
        {
            // traces may point into the folder even when the folder itself is gone
            changes.AddRange(Traces(folder));
            if (WhyNotMove(folder) is { } why) notes.Add(FolderPrefix + why);
            else
            {
                changes.Add(MoveToAblageChange.ForFolder(_files, folder));
                moved = folder;
            }
        }

        changes.Add(new RegKeyChange(_reg, p.Hive, p.RegistryKey, p.DisplayName));
        return new(p, changes, moved, notes);
    }

    // services, then tasks, then autostarts; one change per finding
    List<IChange> Traces(string folder)
    {
        IReadOnlyList<Finding> scanned;
        try { scanned = _traceScan(); }
        catch (Exception ex)
        {
            FileLog.Warn("Suche nach Programmspuren fehlgeschlagen", ex);
            scanned = [];
        }
        var found = ProgramTraces.InFolder(scanned, folder)
            .GroupBy(f => f.Id, StringComparer.Ordinal).Select(g => g.First())
            .Where(f => TraceKinds.Contains(f.Change!.Kind))
            .ToList();
        return TraceKinds.SelectMany(kind => found.Where(f => f.Change!.Kind == kind).Select(f => f.Change!)).ToList();
    }

    string? WhyNotMove(string folder)
    {
        if (!_files.Exists(folder)) return FolderMissing;
        if (_files.InspectFolder(folder) is not { HasReparsePoints: false }) return FolderUnreadable;

        IReadOnlyList<RunningProcess> running;
        try { running = _procs.Snapshot(); }
        catch (Exception ex)
        {
            FileLog.Warn("Prozessliste nicht lesbar", ex);
            return ProcessesUnknown;
        }
        var prefix = folder + "\\";
        foreach (var proc in running)
        {
            if (string.IsNullOrWhiteSpace(proc.ExePath) || !Path.IsPathFullyQualified(proc.ExePath)) continue;
            string full;
            try { full = Path.GetFullPath(proc.ExePath); }
            catch (Exception) { continue; }
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return FolderInUse;
        }
        return null;
    }
}
