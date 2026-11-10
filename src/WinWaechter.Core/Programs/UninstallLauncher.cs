using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static WinWaechter.Core.Sys.Native.JobNative;

namespace WinWaechter.Core.Programs;

public interface IUninstallLauncher
{
    /// <summary>Starts the command and returns the started process's exit code, interpreted.</summary>
    Task<UninstallOutcome> RunAsync(UninstallCommand cmd);
}

/// <summary>
/// Starts an uninstaller inside a job object (no shell) and waits until every process of the job has ended,
/// so uninstallers that hand off to a child process are waited for as well. Once the started process itself
/// has ended, other job processes (a browser with a survey, a restarted Explorer) get a short grace period only.
/// </summary>
public sealed class JobUninstallLauncher : IUninstallLauncher
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    readonly TimeSpan _grace;

    public JobUninstallLauncher() : this(DefaultGrace) { }

    internal JobUninstallLauncher(TimeSpan grace) => _grace = grace;

    public async Task<UninstallOutcome> RunAsync(UninstallCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
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
                () => ActiveProcesses(job), _grace, Poll).ConfigureAwait(false);

            WaitForSingleObject(process, INFINITE);
            if (!GetExitCodeProcess(process, out var code))
                throw Fail("Der Rückgabewert des Deinstallationsprogramms konnte nicht gelesen werden");
            return UninstallExit.Interpret(unchecked((int)code), childrenLeft);
        }
    }

    /// <summary>
    /// Waits until no job process is left. After the root process has ended, waits at most grace for the rest;
    /// returns true when job processes were still running then.
    /// </summary>
    internal static async Task<bool> WaitForJobAsync(Func<bool> rootExited, Func<uint> activeProcesses, TimeSpan grace,
        TimeSpan poll)
    {
        DateTime? rootGoneAt = null;
        while (activeProcesses() > 0)
        {
            if (rootExited())
            {
                rootGoneAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - rootGoneAt >= grace) return true;
            }
            await Task.Delay(poll).ConfigureAwait(false);
        }
        return false;
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

    static uint ActiveProcesses(SafeKernelHandle job)
    {
        if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
            throw Fail("Der Stand der Deinstallation konnte nicht abgefragt werden");
        return info.ActiveProcesses;
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
