using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static WinWaechter.Core.Sys.Native.JobNative;

namespace WinWaechter.Core.Programs;

public interface IUninstallLauncher
{
    /// <summary>Starts the command and returns the exit code of the started process.</summary>
    Task<int> RunAsync(UninstallCommand cmd);
}

/// <summary>
/// Starts an uninstaller inside a job object (no shell) and waits until every process of the job has ended,
/// so uninstallers that hand off to a child process are waited for as well.
/// </summary>
public sealed class JobUninstallLauncher : IUninstallLauncher
{
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    public async Task<int> RunAsync(UninstallCommand cmd)
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

            while (ActiveProcesses(job) > 0) await Task.Delay(Poll).ConfigureAwait(false);

            WaitForSingleObject(process, INFINITE);
            if (!GetExitCodeProcess(process, out var code))
                throw Fail("Der Rückgabewert des Deinstallationsprogramms konnte nicht gelesen werden");
            return unchecked((int)code);
        }
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

/// <summary>How a finished uninstall ended, from its exit code.</summary>
public sealed record UninstallOutcome(int ExitCode, bool Cancelled, bool RestartRequired);

public static class UninstallExit
{
    const int UserCancelled = 1602, RebootRequired = 3010, RebootInitiated = 1641;

    public static UninstallOutcome Interpret(int code) =>
        new(code, code == UserCancelled, code is RebootRequired or RebootInitiated);
}
