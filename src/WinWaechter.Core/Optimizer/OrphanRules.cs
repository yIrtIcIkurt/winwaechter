using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer;

/// <summary>Decides whether an autostart, task or service entry points to a program file that is really gone.</summary>
public static class OrphanRules
{
    public const string OrphanReason = "Programmdatei existiert nicht mehr (Deinstallationsrest)";

    /// <summary>
    /// True only when the file is definitely missing and not under the Windows directory. With a user,
    /// per-user variables are resolved for that user (see ProgramPresence.Check).
    /// </summary>
    public static bool IsOrphan(string? exe, IFileOps files, string windowsDir, IUserContext? user = null)
    {
        if (ProgramPresence.Check(exe, files, user) != Presence.Missing) return false;
        var p = user is null ? PathNormalizer.NormalizePath(exe!) : PathNormalizer.NormalizeForUser(exe!, user);
        return string.IsNullOrEmpty(windowsDir)
            || !p.StartsWith(windowsDir.TrimEnd('\\') +"\\", StringComparison.OrdinalIgnoreCase);
    }
}
