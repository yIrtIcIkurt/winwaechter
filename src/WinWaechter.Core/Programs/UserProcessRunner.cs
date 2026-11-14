using System.Runtime.InteropServices;
using static WinWaechter.Core.Sys.Native.JobNative;

namespace WinWaechter.Core.Programs;

/// <summary>Runs a program with the signed-in user's normal rights and waits for its exit code.</summary>
public interface IUserProcessRunner
{
    /// <summary>Throws (UserLaunchException) when the start is not possible; never falls back to elevated rights.
    /// Cancelling only stops the wait.</summary>
    Task<int> RunAsUserAsync(string exe, string args, CancellationToken ct);
}

/// <summary>Same start as <see cref="UserTokenLauncher"/> (user's Explorer token), but waits for the end.</summary>
public sealed class UserProcessRunner(string? userSid) : IUserProcessRunner
{
    const uint WaitTimeout = 0x102;

    public Task<int> RunAsUserAsync(string exe, string args, CancellationToken ct) => Task.Run(() =>
    {
        var (process, thread) = UserProcess.StartSuspended(new UninstallCommand(exe, args), userSid);
        using (process)
        using (thread)
        {
            if (ResumeThread(thread) == uint.MaxValue)
            {
                var err = Marshal.GetLastWin32Error();
                TerminateProcess(process, 1);   // still suspended, nothing ran
                throw UserProcess.NoLaunch(err);
            }
            while (WaitForSingleObject(process, 250) == WaitTimeout) ct.ThrowIfCancellationRequested();
            return GetExitCodeProcess(process, out var code) ? unchecked((int)code) : -1;
        }
    }, ct);
}
