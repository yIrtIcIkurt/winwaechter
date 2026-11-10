using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;

namespace WinWaechter.Core.Programs;

/// <summary>Picks the scanner findings (services, tasks, autostarts) that belong to one program.</summary>
public static class ProgramTraces
{
    /// <summary>Findings with a change whose program path lies inside folder.</summary>
    public static IReadOnlyList<Finding> InFolder(IEnumerable<Finding> scanned, string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        return scanned.Where(f => f.Change is not null && Full(f.ExePath) is { } exe
            && exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Orphaned entries with a change whose title or location names the program at a word start.</summary>
    public static IReadOnlyList<Finding> ByName(IEnumerable<Finding> scanned, string displayName)
    {
        var key = NameKey(displayName);
        if (key.Length < 3) return [];   // too short to tell programs apart
        return scanned.Where(f => f.Change is not null && f.Group == FindingGroups.OrphanEntries
            && (WordStartContains(InstalledProgramIndex.Normalize(f.Title), key)
                || WordStartContains(InstalledProgramIndex.Normalize(f.Location), key))).ToList();
    }

    /// <summary>Normalized name without trailing version tokens ("FooTool 2.3.1" → "footool").</summary>
    internal static string NameKey(string displayName)
    {
        var tokens = InstalledProgramIndex.Normalize(displayName).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && tokens[^1].All(c => char.IsDigit(c) || c == '.')) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    static bool WordStartContains(string text, string needle)
    {
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = i + 1 < text.Length ? text.IndexOf(needle, i + 1, StringComparison.Ordinal) : -1)
            if (i == 0 || text[i - 1] == ' ') return true;
        return false;
    }

    static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }
}
