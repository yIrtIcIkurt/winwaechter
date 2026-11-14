using System.Text.RegularExpressions;

namespace WinWaechter.Core.Updates;

/// <summary>
/// Reads the table winget prints for „upgrade“. winget has no JSON output for it, so columns are cut at the
/// positions the header line gives (the words above the dash line). Headers are never interpreted (they are
/// localized); the column order Name, ID, Version, Available[, Source] is the same in every language.
/// Widths are in display cells: winget pads East-Asian wide characters as two.
/// </summary>
public static partial class WingetTable
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}$")]
    private static partial Regex IdPattern();

    /// <summary>A winget package id WinWächter accepts (also the guard before any command line is built).</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern().IsMatch(id);

    /// <summary>Rows of the first table. A row with a broken id (truncated „…“ or unexpected characters) is
    /// dropped and reported through onDiscard; the table ends at an empty or short line (the footer).</summary>
    public static IReadOnlyList<(string Name, string Id, string Version, string Available, string Source)> Parse(
        string stdout, Action<string>? onDiscard = null)
    {
        var rows = new List<(string, string, string, string, string)>();
        // CR LF is one line end; a lone CR (spinner, progress redraw) ends a line too
        var lines = stdout.Replace("\r\n", "\n").Split(['\r', '\n']);
        var dash = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length >= 10 && l.All(c => c == '-')) { dash = i; break; }
        }
        if (dash < 0) return rows;

        // the header is the last non-empty line above the dash line
        var h = dash - 1;
        while (h >= 0 && lines[h].Trim().Length == 0) h--;
        if (h < 0) return rows;
        var header = lines[h];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count is not (4 or 5))
        {
            onDiscard?.Invoke("Unbekannter Tabellenkopf: " + header.Trim());
            return rows;
        }

        for (var i = dash + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) break;
            if (trimmed.Length >= 10 && trimmed.All(c => c == '-')) break;   // a second table
            var cells = Cut(line, starts, out var aligned);
            if (cells is null) break;   // shorter than the last column: footer text
            var id = cells[1];
            // every cell must begin after a space: otherwise the row is shifted (or it is a long footer) – never guess
            if (!aligned || !IsValidId(id))
            {
                onDiscard?.Invoke(line.Trim());
                continue;
            }
            rows.Add((cells[0], id, cells[2], cells[3], starts.Count == 5 ? cells[4] : ""));
        }
        return rows;
    }

    // Cells of one row by display position; null when the row does not reach the last column.
    // aligned: the character before each column start is a space (a row shifted by wrong decoding is not).
    static string[]? Cut(string line, List<int> starts, out bool aligned)
    {
        var parts = new System.Text.StringBuilder[starts.Count];
        for (var k = 0; k < parts.Length; k++) parts[k] = new System.Text.StringBuilder();
        var cell = 0;
        var col = 0;
        var reachedLast = false;
        var previous = ' ';
        aligned = true;
        foreach (var c in line)
        {
            while (col + 1 < starts.Count && cell >= starts[col + 1])
            {
                col++;
                if (previous != ' ') aligned = false;
            }
            if (col == starts.Count - 1 && c != ' ') reachedLast = true;
            parts[col].Append(c);
            cell += Width(c);
            previous = c;
        }
        if (!reachedLast) return null;
        return parts.Select(p => p.ToString().Trim()).ToArray();
    }

    /// <summary>Display cells of a character (2 for East-Asian wide ranges).</summary>
    public static int Width(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣'
            or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠'
            or >= '￠' and <= '￦'
            ? 2
            : 1;
}
