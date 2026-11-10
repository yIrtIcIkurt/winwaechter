using System.Globalization;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>
/// Reads the uninstall entries and flags them with the rules: by default only the visible programs,
/// with includeHidden every entry (system components, updates, nameless ones) for overlap checks.
/// </summary>
public sealed class ProgramInventory(IRegistry reg, RuleSet rules)
{
    static readonly string[] UpdateTypes = ["Update", "Hotfix", "Security Update"];

    /// <summary>
    /// Visible programs sorted by name; subkeys that cannot be read are skipped. includeHidden also returns
    /// system components, updates and nameless entries (named after their key), e.g. for folder-overlap checks.
    /// </summary>
    public IReadOnlyList<InstalledProgram> Read(bool includeHidden = false)
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, root) in UninstallRoots.All)
        {
            IReadOnlyList<string> names;
            try { names = reg.SubKeyNames(hive, root); }
            catch (Exception) { continue; }
            foreach (var name in names)
            {
                try
                {
                    if (ReadEntry(hive, root, name, includeHidden) is { } p) list.Add(p);
                }
                catch (Exception) { /* unreadable entry: skip */ }
            }
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    InstalledProgram? ReadEntry(Hive hive, string root, string name, bool includeHidden)
    {
        var key = root + "\\" + name;
        var display = Str(hive, key, "DisplayName")?.Trim();
        if (!includeHidden)
        {
            if (Dword(hive, key, "SystemComponent") == 1) return null;
            if (reg.Get(hive, key, "ParentKeyName") is not null) return null;
            if (string.IsNullOrEmpty(display)) return null;
            if (Str(hive, key, "ReleaseType") is { } rt && UpdateTypes.Contains(rt.Trim(), StringComparer.OrdinalIgnoreCase)) return null;
        }
        if (string.IsNullOrEmpty(display)) display = name;

        var publisher = Str(hive, key, "Publisher");
        var location = Str(hive, key, "InstallLocation");
        var icon = Str(hive, key, "DisplayIcon");
        DateTime? date = DateTime.TryParseExact(Str(hive, key, "InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
        long? size = Dword(hive, key, "EstimatedSize") is > 0 and var kb ? kb * 1024 : null;

        var exe = IconExe(icon) ?? (location is { } l ? l.Trim('"').TrimEnd('\\') + "\\" : null);
        var flag = rules.Classify(new ItemFacts(display, exe, publisher, null)) switch
        {
            RuleVerdict.Protected => ProgramFlag.Protected,
            RuleVerdict.Pua => ProgramFlag.Pua,
            RuleVerdict.Miner => ProgramFlag.Miner,
            _ => ProgramFlag.None,
        };
        return new InstalledProgram(hive, root, name, display, publisher, Str(hive, key, "DisplayVersion"), date, size,
            Str(hive, key, "UninstallString"), location, icon, Dword(hive, key, "WindowsInstaller") == 1, flag);
    }

    string? Str(Hive h, string key, string name) => reg.Get(h, key, name) as string;

    long? Dword(Hive h, string key, string name) =>
        reg.Get(h, key, name) is int i ? (uint)i : reg.Get(h, key, name) is long l ? l : null;

    /// <summary>Exe or icon path from a DisplayIcon value: quotes and ",&lt;n&gt;" suffix removed; null when empty.</summary>
    internal static string? IconExe(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim();
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        s = s.Trim().Trim('"').Trim();
        return s.Length == 0 ? null : s;
    }
}
