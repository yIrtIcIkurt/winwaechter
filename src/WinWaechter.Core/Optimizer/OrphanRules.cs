using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer;

/// <summary>Decides whether an autostart, task or service entry points to a program file that is really gone.</summary>
public static class OrphanRules
{
    public const string OrphanReason = "Programmdatei existiert nicht mehr (Deinstallationsrest)";

    /// <summary>True only when the file is definitely missing and not under the Windows directory.</summary>
    public static bool IsOrphan(string? exe, IFileOps files, string windowsDir)
    {
        if (ProgramPresence.Check(exe, files) != Presence.Missing) return false;
        var p = PathNormalizer.NormalizePath(exe!);
        return string.IsNullOrEmpty(windowsDir)
            || !p.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
