namespace WinWaechter.Core.Programs;

/// <summary>WinWaechter's own uninstall entry: never uninstalled or force-removed from inside the app.</summary>
public static class OwnEntry
{
    public const string Author = "Tahsin Yilmaz";

    /// <summary>By name („WinWächter …“), publisher or an InstallLocation equal to the app folder.</summary>
    public static bool Is(InstalledProgram p, string appDir)
    {
        var name = p.DisplayName.Trim();
        if (name.StartsWith("WinWächter", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WinWaechter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Publisher?.Trim(), Author, StringComparison.OrdinalIgnoreCase))
            return true;
        return ProgramFolderRules.Normalize(p.InstallLocation) is { } location
            && ProgramFolderRules.Normalize(appDir) is { } own
            && string.Equals(location, own, StringComparison.OrdinalIgnoreCase);
    }
}
