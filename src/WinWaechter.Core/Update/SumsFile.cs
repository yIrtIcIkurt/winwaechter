using System.Text;

namespace WinWaechter.Core.Update;

// The signed SHA256SUMS file. Strict format:
//   version: 0.1.1
//   <64 lowercase hex>  <file name>      (one or more, two spaces)
// LF or CRLF, nothing else.
public sealed class SumsFile
{
    static readonly UTF8Encoding Strict = new(false, true);

    public SemVer Version { get; }
    public IReadOnlyDictionary<string, byte[]> Hashes { get; }

    SumsFile(SemVer v, Dictionary<string, byte[]> h) { Version = v; Hashes = h; }

    public static string Build(SemVer version, string fileName, byte[] sha256)
        => $"version: {version}\n{Convert.ToHexString(sha256).ToLowerInvariant()}  {fileName}\n";

    public static bool TryParse(byte[] bytes, out SumsFile? file)
    {
        file = null;
        string text;
        try { text = Strict.GetString(bytes); } catch (ArgumentException) { return false; }

        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // one final newline
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
            if (lines[i].Contains('\r')) return false;
        }
        if (lines.Count < 2) return false;

        const string prefix = "version: ";
        if (!lines[0].StartsWith(prefix, StringComparison.Ordinal)) return false;
        var vText = lines[0][prefix.Length..];
        if (vText.StartsWith('v') || !SemVer.TryParse(vText, out var version)) return false;
        if (version.ToString() != vText) return false; // no leading zeros etc.

        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Length < 67 || l[64] != ' ' || l[65] != ' ') return false;
            var hex = l[..64];
            foreach (var c in hex)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
            var name = l[66..];
            if (!IsPlainFileName(name)) return false;
            if (!map.TryAdd(name, Convert.FromHexString(hex))) return false; // duplicate name
        }
        file = new SumsFile(version, map);
        return true;
    }

    public static bool IsPlainFileName(string name)
    {
        if (name.Length == 0 || name is "." or "..") return false;
        foreach (var c in name)
            if (c < ' ' || c is '/' or '\\' or ':') return false;
        return true;
    }
}
