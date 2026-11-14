using System.Runtime.InteropServices;

namespace WinWaechter.Core.Sys;

/// <summary>Empties the recycle bin of one drive (final: nothing to undo).</summary>
public interface IRecycleBin
{
    void Empty(string driveRoot);
}

public sealed class WinRecycleBin : IRecycleBin
{
    const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SHEmptyRecycleBinW")]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public void Empty(string driveRoot)
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, driveRoot, NoConfirmation | NoProgressUi | NoSound);
        // S_OK, or E_UNEXPECTED (0x8000FFFF) when it was already empty
        if (hr is not (0 or unchecked((int)0x8000FFFF))) Marshal.ThrowExceptionForHR(hr);
    }
}
