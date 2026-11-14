using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;

namespace WinWaechter.Core.Updates;

/// <summary>
/// Hides what WinWächter must never update: entries of the local <c>updateBlock</c> list (Dell, Lexware,
/// Haufe, driver vendors) by winget name or id, programs whose installed entry (publisher, name, folder) is on
/// that list, and WinWächter itself. The general protection list is not used here: it also protects Microsoft,
/// whose runtimes should be updated.
/// </summary>
public sealed class UpdateFilter(RuleSet rules, Func<IReadOnlyList<InstalledProgram>> installed)
{
    public (IReadOnlyList<WingetPackage> Offered, IReadOnlyList<WingetPackage> Hidden) Split(IReadOnlyList<WingetPackage> all)
    {
        IReadOnlyList<InstalledProgram>? programs = null;
        var offered = new List<WingetPackage>();
        var hidden = new List<WingetPackage>();
        foreach (var p in all)
        {
            if (IsBlocked(p, programs ??= installed())) hidden.Add(p);
            else offered.Add(p);
        }
        return (offered, hidden);
    }

    bool IsBlocked(WingetPackage p, IReadOnlyList<InstalledProgram> programs)
    {
        if (IsOwn(p) || rules.IsUpdateBlocked(p.Name, null, null, p.Id)) return true;
        return programs.Any(prog => SameName(p.Name, prog.DisplayName)
                                    && rules.IsUpdateBlocked(prog.DisplayName, prog.Publisher, prog.InstallLocation));
    }

    static bool IsOwn(WingetPackage p) =>
        p.Id.StartsWith("TahsinYilmaz.WinWaechter", StringComparison.OrdinalIgnoreCase)
        || p.Name.StartsWith("WinWächter", StringComparison.OrdinalIgnoreCase);

    // winget shows the uninstall entry's name, cut with „…“ when the column is too narrow
    static bool SameName(string wingetName, string displayName)
    {
        var w = wingetName.Trim();
        var d = displayName.Trim();
        if (w.EndsWith('…')) return w.Length > 1 && d.StartsWith(w[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(w, d, StringComparison.OrdinalIgnoreCase);
    }
}
