using System.Runtime.InteropServices;

namespace WinWaechter.Core.Sys.Native;

internal static class SpiNative
{
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetInt(uint action, uint param, out int value, uint flags);

    // For the SET action the value itself travels in pvParam.
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetValue(uint action, uint param, IntPtr value, uint flags);
}
