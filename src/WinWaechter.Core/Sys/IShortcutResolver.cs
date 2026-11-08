namespace WinWaechter.Core.Sys;

public interface IShortcutResolver
{
    /// <summary>Target path of a .lnk file, or null if it cannot be resolved.</summary>
    string? ResolveTarget(string lnkPath);
}
