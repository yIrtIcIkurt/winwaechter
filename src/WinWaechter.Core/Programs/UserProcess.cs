using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WinWaechter.Core.Sys;
using static WinWaechter.Core.Sys.Native.JobNative;
using static WinWaechter.Core.Sys.Native.UserTokenNative;
using UserNative = WinWaechter.Core.Sys.Native.UserNative;

namespace WinWaechter.Core.Programs;

/// <summary>
/// Starts a process with the signed-in user's normal rights from the elevated app: the token of the user's
/// Explorer in the console session is duplicated and used with CreateProcessWithTokenW (what the Windows
/// settings effectively do). The process never gets elevated rights from here; an uninstaller that demands
/// them fails with ERROR_ELEVATION_REQUIRED and the caller sends the user to the settings instead.
/// </summary>
internal static class UserProcess
{
    // seclogon adjusts the duplicated token (session id, default DACL) before creating the process;
    // without ADJUST_DEFAULT and ADJUST_SESSIONID CreateProcessWithTokenW fails with "access denied".
    const uint DupAccess = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;

    public static (SafeKernelHandle Process, SafeKernelHandle Thread) StartSuspended(UninstallCommand cmd, string? userSid)
    {
        // Only a process that is certainly without admin rights (a debug build) starts it itself.
        if (HasNoAdminRights()) return JobUninstallLauncher.StartSuspended(cmd);
        if (string.IsNullOrEmpty(userSid)) throw NoLaunch(0);

        using var token = UserToken(userSid);
        if (!CreateEnvironmentBlock(out var env, token, false)) throw NoLaunch(Marshal.GetLastWin32Error());
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessWithToken(token, 0, cmd.Exe, JobUninstallLauncher.CommandLine(cmd),
                    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, env, Path.GetDirectoryName(cmd.Exe), ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw err == ERROR_ELEVATION_REQUIRED
                    ? new UserLaunchException(UninstallPlanner.NeedsAdminReason, err)
                    : NoLaunch(err);
            }
            return (new SafeKernelHandle(pi.hProcess), new SafeKernelHandle(pi.hThread));
        }
        finally
        {
            DestroyEnvironmentBlock(env);
        }
    }

    internal static UserLaunchException NoLaunch(int err) => new(UninstallPlanner.NoUserLaunchReason, err);

    // Fails closed: unknown elevation counts as elevated; an enabled Administrators group (UAC off) as well.
    static bool HasNoAdminRights()
    {
        using var id = WindowsIdentity.GetCurrent();
        return !Elevated(id.AccessToken) && !new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool Elevated(SafeAccessTokenHandle token) =>
        !GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) || elevated != 0;

    // Primary token of the given user's Explorer in the active console session; never an elevated one.
    static SafeAccessTokenHandle UserToken(string userSid)
    {
        var session = UserNative.WTSGetActiveConsoleSessionId();
        if (session == UserNative.NoActiveSession) throw NoLaunch(0);
        var lastError = 0;
        var explorers = Process.GetProcessesByName("explorer");
        try
        {
            foreach (var p in explorers)
            {
                try
                {
                    if (p.SessionId != (int)session) continue;
                    using var proc = UserNative.OpenProcess(UserNative.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (proc.IsInvalid) { lastError = Marshal.GetLastWin32Error(); continue; }
                    if (!UserNative.OpenProcessToken(proc, TOKEN_QUERY | TOKEN_DUPLICATE, out var tok))
                    {
                        lastError = Marshal.GetLastWin32Error();
                        continue;
                    }
                    using (tok)
                    {
                        if (!string.Equals(InteractiveUserContext.TokenSid(tok), userSid, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Elevated(tok)) throw NoLaunch(0);   // Explorer runs elevated, or its state is unknown
                        if (!DuplicateTokenEx(tok, DupAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                            throw NoLaunch(Marshal.GetLastWin32Error());
                        return primary;
                    }
                }
                catch (UserLaunchException) { throw; }
                catch (Exception) { /* process gone or not readable: try the next one */ }
            }
        }
        finally
        {
            foreach (var p in explorers) p.Dispose();
        }
        throw NoLaunch(lastError);
    }
}
