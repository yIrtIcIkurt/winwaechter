using System.Text;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Normalized text of every installed program (uninstall entries plus the user's Appx packages),
/// used to decide whether a program folder still belongs to something installed.
/// </summary>
public sealed class InstalledProgramIndex
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Wow64UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    const string AppxPackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    static readonly string[] Fields = ["DisplayName", "Publisher", "InstallLocation", "DisplayIcon", "UninstallString"];

    readonly List<string> _texts;

    InstalledProgramIndex(List<string> texts, int uninstallEntries)
    {
        _texts = texts;
        UninstallEntryCount = uninstallEntries;
    }

    /// <summary>Uninstall entries with at least one readable field (Appx packages not counted).</summary>
    public int UninstallEntryCount { get; }

    public static InstalledProgramIndex Load(IRegistry reg)
    {
        var texts = new List<string>();
        var entries = 0;
        foreach (var (hive, key) in new[] { (Hive.LocalMachine, UninstallKey), (Hive.LocalMachine, Wow64UninstallKey), (Hive.CurrentUser, UninstallKey) })
        {
            foreach (var sub in SafeSubKeys(reg, hive, key))
            {
                var values = Fields.Select(n => SafeString(reg, hive, key + "\\" + sub, n))
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Normalize(v!)).Where(v => v.Length > 0).ToList();
                if (values.Count == 0) continue;
                entries++;
                texts.AddRange(values);
                // the key name is often the product name; one more match only keeps more folders
                texts.Add(Normalize(sub));
            }
        }
        texts.AddRange(SafeSubKeys(reg, Hive.CurrentUser, AppxPackagesKey).Select(Normalize));
        return new InstalledProgramIndex(texts.Where(t => t.Length > 0).Distinct().ToList(), entries);
    }

    /// <summary>True when any installed program text contains the folder name at a word start (names under 3 chars always match).</summary>
    public bool Matches(string folderName)
    {
        var n = Normalize(folderName);
        if (n.Length < 3) return true;
        return _texts.Any(t => WordStartContains(t, n));
    }

    static bool WordStartContains(string text, string needle)
    {
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = i + 1 < text.Length ? text.IndexOf(needle, i + 1, StringComparison.Ordinal) : -1)
            if (i == 0 || text[i - 1] == ' ') return true;
        return false;
    }

    /// <summary>Lower-case (invariant); every run of non-letter/digit characters becomes one space; trimmed.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var gap = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (gap && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
                gap = false;
            }
            else gap = true;
        }
        return sb.ToString();
    }

    // A key we cannot read contributes nothing (and thus never counts as an entry).
    static IReadOnlyList<string> SafeSubKeys(IRegistry reg, Hive h, string key)
    {
        try { return reg.SubKeyNames(h, key); }
        catch (Exception) { return []; }
    }

    static string? SafeString(IRegistry reg, Hive h, string key, string name)
    {
        try { return reg.Get(h, key, name) as string; }
        catch (Exception) { return null; }
    }
}
