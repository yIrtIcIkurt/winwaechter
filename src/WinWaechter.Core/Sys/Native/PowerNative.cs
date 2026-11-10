using System.Runtime.InteropServices;

namespace WinWaechter.Core.Sys.Native;

internal static class PowerNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerSetActiveOverlayScheme(Guid overlayScheme);

    /// <summary>The active power plan; the returned pointer must be freed with <see cref="LocalFree"/>.</summary>
    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern IntPtr LocalFree(IntPtr mem);
}
