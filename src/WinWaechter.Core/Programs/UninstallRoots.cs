using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>The three registry places where installed programs register their uninstall entries.</summary>
public static class UninstallRoots
{
    public const string Native = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Wow64 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string User = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static readonly IReadOnlyList<(Hive Hive, string Key)> All =
    [
        (Hive.LocalMachine, Native),
        (Hive.LocalMachine, Wow64),
        (Hive.CurrentUser, User),
    ];

    /// <summary>
    /// True when key is exactly "root\name" for one of the (hive, root) pairs in <see cref="All"/>:
    /// name non-empty, no backslash, not "." or "..". Roots match case-insensitively for HKLM;
    /// for HKCU the root must be spelled exactly as <see cref="User"/> (so the HKLM text never passes there).
    /// </summary>
    public static bool IsDirectChild(Hive h, string key)
    {
        foreach (var (hive, root) in All)
        {
            if (hive != h || !key.StartsWith(root, hive == Hive.LocalMachine ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            if (key.Length <= root.Length + 1 || key[root.Length] != '\\') continue;
            var name = key[(root.Length + 1)..];
            if (name.Contains('\\') || name is "." or "..") continue;
            return true;
        }
        return false;
    }
}
