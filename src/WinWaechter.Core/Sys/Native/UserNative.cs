using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinWaechter.Core.Sys.Native;

// kernel32/advapi32 declarations to read the user of another process.
internal static class UserNative
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;
    public const uint NoActiveSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        [Out] char[] exeName, ref uint size);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}
