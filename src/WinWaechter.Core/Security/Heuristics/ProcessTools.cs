using System.Diagnostics;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Heuristics;

/// <summary>UI action "Prozess beenden" (not reversible, therefore not an IChange).</summary>
public static class ProcessTools
{
    /// <summary>Kills all processes started from exePath; returns how many were ended.</summary>
    public static int KillByPath(string exePath)
    {
        var target = PathNormalizer.NormalizePath(exePath);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessId;
        if (!IsKillAllowed(target, target, 0, self, windows)) return 0;
        var killed = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!IsKillAllowed(target, path, p.Id, self, windows)) continue;
                    p.Kill();
                    p.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex) { FileLog.Warn($"Prozess {p.Id} konnte nicht beendet werden", ex); }
            }
        }
        return killed;
    }

    /// <summary>Guard: same file, not this process, never anything under the Windows folder.</summary>
    internal static bool IsKillAllowed(string target, string? processPath, int pid, int selfPid, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(processPath) || pid == selfPid) return false;
        var win = windowsDir.TrimEnd('\\') + "\\";
        if (target.StartsWith(win, StringComparison.OrdinalIgnoreCase)
            || processPath.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(PathNormalizer.NormalizePath(processPath), target, StringComparison.OrdinalIgnoreCase);
    }
}
