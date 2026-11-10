using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Sys;

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

    public const int MinNameLength = 4;

    /// <summary>
    /// Orphaned entries with a change whose title or location contains the program's whole normalized name
    /// (version suffix dropped, at least <see cref="MinNameLength"/> characters) as complete words, and whose
    /// program file is definitely missing. A live entry of another program that shares a word is never returned.
    /// </summary>
    public static IReadOnlyList<Finding> ByName(IEnumerable<Finding> scanned, string displayName, IFileOps files)
    {
        var key = NameKey(displayName);
        if (key.Length < MinNameLength) return [];   // too short to tell programs apart
        return scanned.Where(f => f.Change is not null && f.Group == FindingGroups.OrphanEntries
            && (ContainsWords(InstalledProgramIndex.Normalize(f.Title), key)
                || ContainsWords(InstalledProgramIndex.Normalize(f.Location), key))
            && ProgramPresence.Check(f.ExePath, files) == Presence.Missing).ToList();
    }

    /// <summary>Normalized name without trailing version tokens ("FooTool 2.3.1" → "footool").</summary>
    internal static string NameKey(string displayName)
    {
        var tokens = InstalledProgramIndex.Normalize(displayName).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && tokens[^1].All(c => char.IsDigit(c) || c == '.')) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    // needle occurs in text with a word boundary (start/end or ' ') on both sides; both are normalized
    static bool ContainsWords(string text, string needle)
    {
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = i + 1 < text.Length ? text.IndexOf(needle, i + 1, StringComparison.Ordinal) : -1)
        {
            var end = i + needle.Length;
            if ((i == 0 || text[i - 1] == ' ') && (end == text.Length || text[end] == ' ')) return true;
        }
        return false;
    }

    static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }
}
