using WinWaechter.Core.Logging;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>Result of a folder check: exactly one of Folder (normalized, accepted) or Refusal (German reason) is set.</summary>
public sealed record FolderCheck(string? Folder, string? Refusal);

public enum FolderState { Missing, Unreadable, Ready }

/// <summary>Whether a folder can be moved as a whole: Facts is set only for Ready.</summary>
public sealed record FolderInspection(FolderState State, FolderFacts? Facts);

/// <summary>Decides whether a program's InstallLocation may be offered for forced removal (spec 7.4).</summary>
public static class ProgramFolderRules
{
    public const string NoFolder = "Kein Installationsordner eingetragen.";
    public const string NotLocal = "Der Installationsordner ist keine gültige lokale Pfadangabe.";
    public const string OutsideRoots = "Nur Ordner unter „Programme“ oder „AppData\\Local\\Programs“ werden angeboten.";
    public const string SharedFolder = "Gemeinsam genutzte Ordner werden nie angeboten.";
    public const string ProtectedFolder = "Der Ordner ist geschützt.";
    public const string UsedByOther = "Ein anderes installiertes Programm nutzt diesen Ordner.";

    static readonly string[] SharedSegments = ["Common Files", "WindowsApps"];
    static readonly char[] BadChars = [':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Blanks and quotes trimmed, environment variables expanded, full path without trailing '\'.
    /// Null for empty values, UNC/device paths, paths that are not fully qualified, or on any error.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"').Trim());
            if (s.Length < 3 || s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(s) || s.IndexOfAny(BadChars, 2) >= 0 || s.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return null;
            var full = Path.GetFullPath(s).TrimEnd('\\');
            return full.Length == 0 ? null : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static FolderCheck Check(string? rawFolder, InstalledProgram self, IReadOnlyList<InstalledProgram> all,
        CleanerPaths paths, RuleSet rules)
    {
        if (string.IsNullOrWhiteSpace(rawFolder)) return Refuse(NoFolder);
        if (Normalize(rawFolder) is not { } folder) return Refuse(NotLocal);

        // a real subfolder (at least one level) of one of the allowed roots
        string?[] roots = [paths.ProgramFiles, paths.ProgramFilesX86, Path.Combine(paths.LocalAppData, "Programs")];
        var root = roots.Select(Normalize).FirstOrDefault(r => r is not null && IsInside(folder, r));
        if (root is null) return Refuse(OutsideRoots);

        var segments = folder[(root.Length + 1)..].Split('\\');
        if (segments.Any(seg => SharedSegments.Contains(seg, StringComparer.OrdinalIgnoreCase))) return Refuse(SharedFolder);

        // vendor collection folders (e.g. "Google", "NVIDIA Corporation") are never offered as a whole
        var name = Path.GetFileName(folder);
        if (rules.IsLeftoverIgnored(name)) return Refuse(SharedFolder);
        if (rules.Classify(new ItemFacts(name, folder + "\\", null, null)) == RuleVerdict.Protected
            || rules.Classify(new ItemFacts(self.DisplayName, folder + "\\", self.Publisher, null)) == RuleVerdict.Protected)
            return Refuse(ProtectedFolder);

        foreach (var other in all)
        {
            if (string.Equals(other.Id, self.Id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var used in FoldersOf(other))
                if (used.Equals(folder, StringComparison.OrdinalIgnoreCase) || IsInside(used, folder) || IsInside(folder, used))
                    return Refuse(UsedByOther);
        }
        return new FolderCheck(folder, null);
    }

    static FolderCheck Refuse(string reason) => new(null, reason);

    /// <summary>Missing, Unreadable (access denied or a reparse point anywhere inside) or Ready with its facts.</summary>
    public static FolderInspection Inspect(IFileOps files, string folder)
    {
        if (!files.Exists(folder)) return new(FolderState.Missing, null);
        return files.InspectFolder(folder) is { HasReparsePoints: false } facts
            ? new(FolderState.Ready, facts)
            : new(FolderState.Unreadable, null);
    }

    /// <summary>True when a running process was started from inside folder; null when the process list cannot be read.</summary>
    public static bool? RunsFromFolder(IProcessList procs, string folder)
    {
        IReadOnlyList<RunningProcess> running;
        try { running = procs.Snapshot(); }
        catch (Exception ex)
        {
            FileLog.Warn("Prozessliste nicht lesbar", ex);
            return null;
        }
        var prefix = folder.TrimEnd('\\') + "\\";
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

    static bool IsInside(string path, string dir) => path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);

    // Folders another entry points at: InstallLocation, the DisplayIcon's folder and the uninstaller's folder.
    static IEnumerable<string> FoldersOf(InstalledProgram p)
    {
        if (Normalize(p.InstallLocation) is { } location) yield return location;
        if (Normalize(ProgramInventory.IconExe(p.DisplayIcon)) is { } icon && Path.GetDirectoryName(icon) is { Length: > 0 } iconDir)
            yield return iconDir.TrimEnd('\\');
        if (Normalize(UninstallCommandParser.Parse(p.UninstallString, File.Exists)?.Exe) is { } exe
            && Path.GetDirectoryName(exe) is { Length: > 0 } exeDir)
            yield return exeDir.TrimEnd('\\');
    }
}
