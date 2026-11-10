using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WinWaechter.Core.Sys.Native.JobNative;

namespace WinWaechter.Core.Sys.Native;

// advapi32/userenv declarations to start a process with the signed-in user's (unelevated) token.
internal static class UserTokenNative
{
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenElevation = 20;
    public const int ERROR_ELEVATION_REQUIRED = 740;

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length,
        out int returnLength);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessWithTokenW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessWithToken(SafeAccessTokenHandle token, uint logonFlags, string applicationName,
        StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("userenv.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
