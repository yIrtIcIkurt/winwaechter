using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

public enum UninstallRoute { Launch, LaunchAsUser, OpenSettings }

/// <summary>Launch = WinWaechter starts Command itself (elevated); LaunchAsUser = WinWaechter starts Command with the
/// signed-in user's normal rights, like the Windows settings do; OpenSettings = send the user to ms-settings:appsfeatures.
/// InstallLocation (Launch routes only): the program folder, so the launcher can tell its uninstaller copies apart.
/// Reason (LaunchAsUser): why the program is not started elevated (for the log; the settings dialog after a failed
/// user launch names the launch problem instead).</summary>
public sealed record UninstallPlan(UninstallRoute Route, UninstallCommand? Command, string Reason, bool UninstallerMissing,
    string? InstallLocation = null);

public static class UninstallPlanner
{
    public const string UnsafeReason = "Der Deinstallationsbefehl kann nicht sicher gestartet werden.";
    public const string MissingReason = "Das Deinstallationsprogramm fehlt.";
    public const string UserWritableReason =
        "Dieses Programm wurde für Ihr Benutzerkonto installiert oder liegt in einem Ordner, den auch Programme ohne Adminrechte ändern können.";
    public const string NeedsAdminReason =
        "Das Deinstallationsprogramm verlangt Adminrechte, liegt aber in einem Ordner, den auch Programme ohne Adminrechte ändern können.";
    public const string NoUserLaunchReason =
        "Das Deinstallationsprogramm ließ sich nicht mit Ihren normalen Benutzerrechten starten.";

    const string MsiExecName = "msiexec.exe";

    // Spec 7.1: only HKLM entries whose uninstaller sits in an admin-only folder are started elevated.
    // Per-user entries and uninstallers in folders that non-admins can change are started with the signed-in
    // user's normal rights (LaunchAsUser), exactly what the Windows settings would do; never elevated.
    // msiexec only ever runs as "/X{GUID}" with a validated product code, never with the entry's arguments.
    public static UninstallPlan Plan(InstalledProgram p, CleanerPaths paths, Func<string, bool> exists, IPathTrust trust)
    {
        var guid = UninstallCommandParser.MsiProductCode(p);
        if (guid is not null)
        {
            var msiexec = Path.Combine(paths.SystemRoot, "System32", MsiExecName);
            if (!IsTrusted(msiexec, Root(paths.SystemRoot), trust)) return Settings(UnsafeReason);
            var msi = new UninstallCommand(msiexec, "/X" + guid);
            return p.IsMachineEntry
                ? new(UninstallRoute.Launch, msi, "", false, p.InstallLocation)
                : AsUser(msi, p);
        }

        // no command at all: the uninstaller is missing (forced removal may be offered, spec 1.3)
        if (string.IsNullOrWhiteSpace(p.UninstallString)) return new(UninstallRoute.OpenSettings, null, MissingReason, true);

        var cmd = UninstallCommandParser.Parse(p.UninstallString, exists);
        if (cmd is null) return Settings(UnsafeReason);

        // Checks run on the normalized path (trailing dots/blanks, "..", etc. resolved) that is later started.
        var full = FullPath(cmd.Exe);
        if (full is null || IsRefused(cmd.Exe) || IsRefused(full)
            || !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            return Settings(UnsafeReason);

        if (!exists(full)) return new(UninstallRoute.OpenSettings, null, MissingReason, true);

        // Under %SystemRoot% only the msiexec route above may be launched.
        if (Root(paths.SystemRoot) is { } sysRoot && full.StartsWith(sysRoot + "\\", StringComparison.OrdinalIgnoreCase))
            return Settings(UnsafeReason);

        if (!p.IsMachineEntry || AdminRoot(full, paths) is not { } root || !IsTrusted(full, root, trust))
            return AsUser(cmd with { Exe = full }, p);

        return new(UninstallRoute.Launch, cmd with { Exe = full }, "", false, p.InstallLocation);
    }

    static UninstallPlan Settings(string reason) => new(UninstallRoute.OpenSettings, null, reason, false);

    static UninstallPlan AsUser(UninstallCommand cmd, InstalledProgram p) =>
        new(UninstallRoute.LaunchAsUser, cmd, UserWritableReason, false, p.InstallLocation);

    // Script hosts, and msiexec in any form other than the validated "/X{GUID}" above.
    static bool IsRefused(string exe)
    {
        var name = Path.GetFileName(exe);
        return UninstallCommandParser.RefusedHosts.Contains(name)
            || string.Equals(name, MsiExecName, StringComparison.OrdinalIgnoreCase);
    }

    // The exe and every folder from its own up to and including the root must be admin-only.
    static bool IsTrusted(string full, string? root, IPathTrust trust)
    {
        if (root is null || !trust.IsAdminOnly(full)) return false;
        for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!trust.IsAdminOnly(dir)) return false;
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    static string? Root(string? dir) =>
        string.IsNullOrWhiteSpace(dir) || FullPath(dir) is not { } f ? null : Path.TrimEndingDirectorySeparator(f);

    // The admin-only root the file lies under (prefix + "\"), or null.
    static string? AdminRoot(string full, CleanerPaths paths)
    {
        string?[] candidates =
        [
            paths.ProgramFiles, paths.ProgramFilesX86, Path.Combine(paths.ProgramData, "Package Cache"),
        ];
        foreach (var c in candidates)
        {
            var root = Root(c);
            if (root is not null && full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return root;
        }
        return null;
    }
}
