using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

public enum UninstallRoute { Launch, OpenSettings }

/// <summary>Launch = WinWaechter starts Command itself; OpenSettings = send the user to ms-settings:appsfeatures.</summary>
public sealed record UninstallPlan(UninstallRoute Route, UninstallCommand? Command, string Reason, bool UninstallerMissing);

public static class UninstallPlanner
{
    public const string UnsafeReason = "Der Deinstallationsbefehl kann nicht sicher gestartet werden.";
    public const string MissingReason = "Das Deinstallationsprogramm fehlt.";
    public const string UserWritableReason =
        "Dieses Programm wurde für Ihr Benutzerkonto installiert oder liegt in einem Ordner, den auch Programme ohne Adminrechte ändern können.";

    // Spec 7.1: only HKLM entries whose uninstaller sits in an admin-only folder are started elevated.
    public static UninstallPlan Plan(InstalledProgram p, CleanerPaths paths, Func<string, bool> exists)
    {
        var guid = UninstallCommandParser.MsiProductCode(p);
        if (guid is not null)
            return p.IsMachineEntry
                ? new(UninstallRoute.Launch,
                    new UninstallCommand(Path.Combine(paths.SystemRoot, "System32", "msiexec.exe"), "/X" + guid), "", false)
                : Settings(UserWritableReason);

        var cmd = UninstallCommandParser.Parse(p.UninstallString, exists);
        if (cmd is null) return Settings(UnsafeReason);

        // Checks run on the normalized path (trailing dots/blanks, "..", etc. resolved) that is later started.
        var full = FullPath(cmd.Exe);
        if (full is null
            || UninstallCommandParser.RefusedHosts.Contains(Path.GetFileName(cmd.Exe))
            || UninstallCommandParser.RefusedHosts.Contains(Path.GetFileName(full))
            || !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            return Settings(UnsafeReason);

        if (!exists(full)) return new(UninstallRoute.OpenSettings, null, MissingReason, true);

        if (!p.IsMachineEntry || !IsAdminOnlyLocation(full, paths)) return Settings(UserWritableReason);

        return new(UninstallRoute.Launch, cmd with { Exe = full }, "", false);
    }

    static UninstallPlan Settings(string reason) => new(UninstallRoute.OpenSettings, null, reason, false);

    static string? FullPath(string exe)
    {
        try { return Path.GetFullPath(exe); }
        catch { return null; }
    }

    static bool IsAdminOnlyLocation(string full, CleanerPaths paths)
    {
        string[] roots =
        [
            paths.ProgramFiles, paths.ProgramFilesX86, paths.SystemRoot, Path.Combine(paths.ProgramData, "Package Cache"),
        ];
        return roots.Any(r => IsUnder(full, r));
    }

    static bool IsUnder(string full, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var prefix = Path.TrimEndingDirectorySeparator(root) + "\\";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
