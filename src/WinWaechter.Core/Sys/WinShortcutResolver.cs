using System.Runtime.InteropServices;

namespace WinWaechter.Core.Sys;

/// <summary>Resolves .lnk targets through the WScript.Shell COM object. Never throws.</summary>
public sealed class WinShortcutResolver : IShortcutResolver
{
    public string? ResolveTarget(string lnkPath)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            dynamic link = ((dynamic)shell).CreateShortcut(lnkPath);
            string? target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
        finally
        {
            try { if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
            catch { /* ignore */ }
        }
    }
}
