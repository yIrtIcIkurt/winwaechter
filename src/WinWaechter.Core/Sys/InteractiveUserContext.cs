using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using WinWaechter.Core.Logging;
using static WinWaechter.Core.Sys.Native.UserNative;

namespace WinWaechter.Core.Sys;

/// <summary>
/// The user who owns explorer.exe in the active console session. Falls back to the
/// current process user if that cannot be determined.
/// </summary>
public sealed class InteractiveUserContext : IUserContext
{
    const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public string Sid { get; }
    public string ProfileDir { get; }
    public string TempDir { get; }
    public string StartupDir { get; }
    public string CommonStartupDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    public InteractiveUserContext()
    {
        try
        {
            Sid = ConsoleExplorerSid();
            ProfileDir = ProfilePath(Sid);
            TempDir = Path.Combine(ProfileDir, "AppData", "Local", "Temp");
            StartupDir = Path.Combine(ProfileDir, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        }
        catch (Exception ex)
        {
            FileLog.Warn("Angemeldeter Benutzer nicht ermittelbar, verwende aktuellen Prozessbenutzer.", ex);
            Sid = WindowsIdentity.GetCurrent().User!.Value;
            ProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TempDir = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            StartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }
    }

    static string ConsoleExplorerSid()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == NoActiveSession) throw new InvalidOperationException("Keine aktive Konsolensitzung.");

        Exception? last = null;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != (int)session) continue;
                    return ProcessUserSid(p.Id);
                }
                catch (Exception ex) { last = ex; }
            }
        }
        throw new InvalidOperationException("Kein explorer.exe in der Konsolensitzung gefunden.", last);
    }

    static string ProcessUserSid(int pid)
    {
        using var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(proc, TOKEN_QUERY, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var len);
            if (len <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buf, len, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the PSID.
                return new SecurityIdentifier(Marshal.ReadIntPtr(buf)).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    static string ProfilePath(string sid)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey($@"{ProfileList}\{sid}");
        var p = k?.GetValue("ProfileImagePath") as string; // expanded by GetValue
        if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p))
            throw new DirectoryNotFoundException($"Profilpfad für {sid} nicht gefunden.");
        return p;
    }
}
