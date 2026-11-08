using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinWaechter.Core.Sys.Native;

// Resolves where a folder really is (links followed) and its literal long-name form (links not followed).
internal static class FinalPathNative
{
    const uint FILE_READ_ATTRIBUTES = 0x0080;
    const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;   // needed to open a directory
    const uint FILE_NAME_NORMALIZED = 0x0;
    const uint VOLUME_NAME_DOS = 0x0;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint size);

    /// <summary>Final path of an existing folder, or null when it cannot be opened/resolved.</summary>
    public static string? Final(string dir)
    {
        using var h = CreateFile(dir, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(512);
        var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (n >= sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        }
        return n == 0 || n >= sb.Capacity ? null : sb.ToString();
    }

    /// <summary>The path with 8.3 components expanded (no link resolution), or null.</summary>
    public static string? LongName(string path)
    {
        var sb = new StringBuilder(512);
        var n = GetLongPathName(path, sb, (uint)sb.Capacity);
        if (n >= sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetLongPathName(path, sb, (uint)sb.Capacity);
        }
        return n == 0 || n >= sb.Capacity ? null : sb.ToString();
    }
}
