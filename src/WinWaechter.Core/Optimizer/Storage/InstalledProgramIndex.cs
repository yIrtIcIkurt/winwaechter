using System.Text;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Normalized text of every installed program (uninstall entries plus the user's Appx packages),
/// used to decide whether a program folder still belongs to something installed.
/// </summary>
public sealed class InstalledProgramIndex
{
    const string AppxPackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    static readonly string[] Fields = ["DisplayName", "Publisher", "InstallLocation", "DisplayIcon", "UninstallString"];

    readonly List<string> _texts;

    InstalledProgramIndex(List<string> texts, int uninstallEntries, int machineEntries, bool readFailed)
    {
        _texts = texts;
        UninstallEntryCount = uninstallEntries;
        MachineEntryCount = machineEntries;
        ReadFailed = readFailed;
    }

    /// <summary>Uninstall entries with at least one readable field (Appx packages not counted).</summary>
    public int UninstallEntryCount { get; }

    /// <summary>Of those, the entries under the native HKLM Uninstall key (64-bit view).</summary>
    public int MachineEntryCount { get; }

    /// <summary>True when any subkey list or value could not be read; the index is then incomplete.</summary>
    public bool ReadFailed { get; }

    public static InstalledProgramIndex Load(IRegistry reg)
    {
        var texts = new List<string>();
        int entries = 0, machine = 0;
        var failed = false;
        foreach (var (hive, key) in UninstallRoots.All)
        {
            foreach (var sub in SubKeys(reg, hive, key, ref failed))
            {
                var values = new List<string>();
                foreach (var field in Fields)
                    if (Read(reg, hive, key + "\\" + sub, field, ref failed) is { } v && Normalize(v) is { Length: > 0 } n)
                        values.Add(n);
                if (values.Count == 0) continue;
                entries++;
                if (hive == Hive.LocalMachine && key == UninstallRoots.Native) machine++;
                texts.AddRange(values);
                // the key name is often the product name; one more match only keeps more folders
                texts.Add(Normalize(sub));
            }
        }
        texts.AddRange(SubKeys(reg, Hive.CurrentUser, AppxPackagesKey, ref failed).Select(Normalize));
        return new InstalledProgramIndex(texts.Where(t => t.Length > 0).Distinct().ToList(), entries, machine, failed);
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

    // Any read error is remembered: the caller must not trust an incomplete index.
    static IReadOnlyList<string> SubKeys(IRegistry reg, Hive h, string key, ref bool failed)
    {
        try { return reg.SubKeyNames(h, key); }
        catch (Exception) { failed = true; return []; }
    }

    static string? Read(IRegistry reg, Hive h, string key, string name, ref bool failed)
    {
        try { return reg.Get(h, key, name) as string; }
        catch (Exception) { failed = true; return null; }
    }
}
