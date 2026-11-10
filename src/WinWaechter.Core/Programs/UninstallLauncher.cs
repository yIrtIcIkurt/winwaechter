using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinWaechter.Core.Sys;
using static WinWaechter.Core.Sys.Native.JobNative;

namespace WinWaechter.Core.Programs;

public interface IUninstallLauncher
{
    /// <summary>Starts the command and returns the started process's exit code, interpreted.
    /// installLocation: the program's folder; processes running from there count as the uninstaller.</summary>
    Task<UninstallOutcome> RunAsync(UninstallCommand cmd, string? installLocation = null);
}

/// <summary>
/// Starts an uninstaller inside a job object (no shell) and waits until every process of the job has ended,
/// so uninstallers that hand off to a child process are waited for as well. Once the started process itself
/// has ended, job processes that look like uninstaller copies (temp folders, the uninstaller's or the program's
/// folder, or an unreadable path) are still waited for; when only other programs are left (a browser with a
/// survey, a restarted Explorer) they get a short grace period only.
/// </summary>
public sealed class JobUninstallLauncher : IUninstallLauncher
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    const int MaxListedProcesses = 1024;

    readonly TimeSpan _grace;

    public JobUninstallLauncher() : this(DefaultGrace) { }

    internal JobUninstallLauncher(TimeSpan grace) => _grace = grace;

    public async Task<UninstallOutcome> RunAsync(UninstallCommand cmd, string? installLocation = null)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var uninstallerDirs = UninstallerDirs(cmd, installLocation);
        // No KILL_ON_JOB_CLOSE: closing the job never ends the uninstaller.
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw Fail("Das Job-Objekt für die Deinstallation konnte nicht angelegt werden");

        var (process, thread) = StartSuspended(cmd);
        using (process)
        using (thread)
        {
            if (!AssignProcessToJobObject(job, process))
            {
                var err = Marshal.GetLastWin32Error();
                TerminateProcess(process, 1);
                throw Fail("Das Deinstallationsprogramm konnte nicht überwacht werden", err);
            }
            if (ResumeThread(thread) == uint.MaxValue)
            {
                var err = Marshal.GetLastWin32Error();
                TerminateProcess(process, 1);
                throw Fail("Das Deinstallationsprogramm konnte nicht gestartet werden", err);
            }

            var childrenLeft = await WaitForJobAsync(() => WaitForSingleObject(process, 0) == WAIT_OBJECT_0,
                () => ProcessPaths(job), path => IsUninstallerProcess(path, uninstallerDirs), _grace, Poll)
                .ConfigureAwait(false);

            WaitForSingleObject(process, INFINITE);
            if (!GetExitCodeProcess(process, out var code))
                throw Fail("Der Rückgabewert des Deinstallationsprogramms konnte nicht gelesen werden");
            return UninstallExit.Interpret(unchecked((int)code), childrenLeft);
        }
    }

    /// <summary>
    /// Waits until no job process is left (jobPaths: image path per running job process, null = unreadable).
    /// After the root process has ended, keeps waiting while an uninstaller process runs; once only other
    /// programs are left, waits at most grace and returns true when they were still running then.
    /// </summary>
    internal static async Task<bool> WaitForJobAsync(Func<bool> rootExited, Func<IReadOnlyList<string?>> jobPaths,
        Func<string?, bool> isUninstaller, TimeSpan grace, TimeSpan poll)
    {
        DateTime? othersOnlySince = null;
        for (var paths = jobPaths(); paths.Count > 0; paths = jobPaths())
        {
            if (rootExited() && !paths.Any(isUninstaller))
            {
                othersOnlySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - othersOnlySince >= grace) return true;
            }
            else
            {
                othersOnlySince = null;   // the uninstaller (or one of its copies) is still at work
            }
            await Task.Delay(poll).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// True for the uninstaller or one of its copies (NSIS Au_.exe, Inno _iu*.tmp): a path that cannot be read,
    /// a path containing \AppData\Local\Temp\, or one under any of dirs.
    /// </summary>
    internal static bool IsUninstallerProcess(string? path, IReadOnlyList<string> dirs)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase)) return true;
        return dirs.Any(d => path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// %TEMP%, %SystemRoot%\Temp, the uninstaller's folder and the program's folder. A folder that is a drive,
    /// a Program Files root or lies under %SystemRoot% (msiexec) is left out: it would cover every other program.
    /// </summary>
    internal static IReadOnlyList<string> UninstallerDirs(UninstallCommand cmd, string? installLocation)
    {
        var sysRoot = ProgramFolderRules.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var dirs = new List<string>();
        foreach (var temp in new[] { Path.GetTempPath(), sysRoot is null ? null : Path.Combine(sysRoot, "Temp") })
            if (ProgramFolderRules.Normalize(temp) is { } t) dirs.Add(t);

        var tooWide = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Select(ProgramFolderRules.Normalize).OfType<string>().ToList();
        foreach (var dir in new[] { Path.GetDirectoryName(cmd.Exe), installLocation })
        {
            if (ProgramFolderRules.Normalize(dir) is not { } d || d.Length <= 3) continue;
            if (tooWide.Contains(d, StringComparer.OrdinalIgnoreCase)) continue;
            if (sysRoot is not null && (d.Equals(sysRoot, StringComparison.OrdinalIgnoreCase)
                    || d.StartsWith(sysRoot + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            dirs.Add(d);
        }
        return dirs;
    }

    static (SafeKernelHandle Process, SafeKernelHandle Thread) StartSuspended(UninstallCommand cmd)
    {
        var line = new StringBuilder("\"" + cmd.Exe + "\"" + (cmd.Arguments.Length > 0 ? " " + cmd.Arguments : ""));
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcess(cmd.Exe, line, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(cmd.Exe), ref si,
                out var pi))
            throw Fail("Das Deinstallationsprogramm konnte nicht gestartet werden");
        return (new SafeKernelHandle(pi.hProcess), new SafeKernelHandle(pi.hThread));
    }

    // Image paths of the processes still running in the job (null where a path cannot be read).
    static IReadOnlyList<string?> ProcessPaths(SafeKernelHandle job)
    {
        var size = 8 + MaxListedProcesses * IntPtr.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryJobProcessIds(job, JobObjectBasicProcessIdList, buf, size, IntPtr.Zero))
            {
                // the list could not be read (e.g. too many processes): count only, paths unknown = keep waiting
                if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                        Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
                    throw Fail("Der Stand der Deinstallation konnte nicht abgefragt werden");
                return new string?[info.ActiveProcesses];
            }
            var listed = Math.Min(Marshal.ReadInt32(buf, 4), MaxListedProcesses);
            var paths = new List<string?>(listed);
            for (var i = 0; i < listed; i++)
            {
                var pid = Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64();
                paths.Add(pid is > 0 and <= int.MaxValue ? WinProcessList.PathOf((int)pid) : null);
            }
            return paths;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    static InvalidOperationException Fail(string what) => Fail(what, Marshal.GetLastWin32Error());

    static InvalidOperationException Fail(string what, int err) =>
        new($"{what} (Fehler {err}: {new Win32Exception(err).Message}).");
}

/// <summary>How a finished uninstall ended, from the started process's exit code.
/// ChildrenStillRunning: other programs it opened were still running when WinWaechter stopped waiting.</summary>
public sealed record UninstallOutcome(int ExitCode, bool Cancelled, bool RestartRequired, bool ChildrenStillRunning = false);

public static class UninstallExit
{
    const int UserCancelled = 1602, RebootRequired = 3010, RebootInitiated = 1641;

    public static UninstallOutcome Interpret(int code, bool childrenStillRunning = false) =>
        new(code, code == UserCancelled, code is RebootRequired or RebootInitiated, childrenStillRunning);
}
