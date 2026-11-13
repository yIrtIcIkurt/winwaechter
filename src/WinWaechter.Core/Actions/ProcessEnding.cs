using System.Diagnostics;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Actions;

/// <summary>Finds and ends processes by name (without ".exe", ignore case). Callers filter with <see cref="ProcessGuard"/> first.</summary>
public interface IProcessEnder
{
    IReadOnlyList<RunningProcess> Find(string processName);
    int CloseWindows(IReadOnlyList<RunningProcess> p);
    int Kill(IReadOnlyList<RunningProcess> p);
    bool AnyRunning(IReadOnlyList<RunningProcess> p);
}

public sealed class WinProcessEnder(IProcessList procs) : IProcessEnder
{
    static string Bare(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public IReadOnlyList<RunningProcess> Find(string processName)
    {
        var want = Bare(processName.Trim());
        return procs.Snapshot()
            .Where(p => string.Equals(Bare(p.Name), want, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public int CloseWindows(IReadOnlyList<RunningProcess> p) => Each(p, proc => proc.CloseMainWindow());

    public int Kill(IReadOnlyList<RunningProcess> p) =>
        Each(p, proc => { proc.Kill(); return true; });

    public bool AnyRunning(IReadOnlyList<RunningProcess> p)
    {
        foreach (var r in p)
        {
            if (r.Pid <= 0) continue;
            try
            {
                using var proc = Process.GetProcessById(r.Pid);
                if (!proc.HasExited && SameProcess(proc, r)) return true;
            }
            catch (Exception)
            {
                // gone or not accessible: counts as not running
            }
        }
        return false;
    }

    // A PID can be reused; compare the process name before acting.
    static bool SameProcess(Process proc, RunningProcess r)
    {
        try { return string.Equals(proc.ProcessName, Bare(r.Name), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    static int Each(IReadOnlyList<RunningProcess> list, Func<Process, bool> act)
    {
        var done = 0;
        foreach (var r in list)
        {
            if (r.Pid <= 0) continue;
            try
            {
                using var proc = Process.GetProcessById(r.Pid);
                if (!SameProcess(proc, r)) continue;
                if (act(proc)) done++;
            }
            catch (Exception)
            {
                // ended meanwhile or access denied: skip
            }
        }
        return done;
    }
}

public sealed record ProcessGuardContext(string WindowsDir, int SelfPid, string? SelfExe,
    IReadOnlySet<string> ServiceImages, Func<RunningProcess, bool> IsProtected);

/// <summary>Decides which processes may be ended. Fails closed: anything unknown is not allowed.</summary>
public static class ProcessGuard
{
    public static readonly string[] Browsers = ["msedge", "chrome", "opera", "firefox"];

    public static bool MayEnd(RunningProcess p, ProcessGuardContext c) =>
        PassesBase(p, c) && !SafeProtected(p, c);

    /// <summary>Only for the fixed browser list (4.9): the same checks except IsProtected.</summary>
    public static bool MayEndBrowser(RunningProcess p, ProcessGuardContext c)
    {
        var name = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name;
        return Browsers.Contains(name, StringComparer.OrdinalIgnoreCase) && PassesBase(p, c);
    }

    static bool SafeProtected(RunningProcess p, ProcessGuardContext c)
    {
        try { return c.IsProtected(p); }
        catch (Exception) { return true; }
    }

    static bool PassesBase(RunningProcess p, ProcessGuardContext c)
    {
        var exe = Normalize(p.ExePath);
        var windows = Normalize(c.WindowsDir);
        if (exe is null || windows is null) return false;
        if (IsUnder(exe, windows)) return false;
        if (p.Pid != 0 && p.Pid == c.SelfPid) return false;
        if (c.SelfExe is not null)
        {
            var self = Normalize(c.SelfExe);
            if (self is null || string.Equals(self, exe, StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (var image in c.ServiceImages)
        {
            var n = Normalize(image);
            if (n is not null && string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/'); }
        catch (Exception) { return null; }
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
