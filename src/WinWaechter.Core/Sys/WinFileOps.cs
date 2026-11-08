namespace WinWaechter.Core.Sys;

/// <summary>Real file system. Never follows reparse points; moves use \\?\ long paths.</summary>
public sealed class WinFileOps : IFileOps
{
    static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    // Hidden and system files count too (the default options would skip them).
    static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age)
    {
        if (!Directory.Exists(dir)) return [];
        var cutoff = DateTime.UtcNow - age;
        return new DirectoryInfo(dir).EnumerateFiles("*", Recursive).Where(f => f.LastWriteTimeUtc < cutoff);
    }

    public IEnumerable<FileInfo> EnumerateTopLevel(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", TopLevel) : [];

    // Missing parent folders are created one at a time; one that turns out to be a reparse point aborts.
    public void Move(string src, string dst)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(dst));
        if (!string.IsNullOrEmpty(parent)) CreateChain(parent);
        File.Move(LongPath(src), LongPath(dst), overwrite: false);
    }

    static void CreateChain(string dir)
    {
        var missing = new Stack<string>();
        for (var d = dir; !string.IsNullOrEmpty(d) && !Directory.Exists(LongPath(d)); d = Path.GetDirectoryName(d))
            missing.Push(d);
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            Directory.CreateDirectory(LongPath(d));
            if (IsReparsePoint(d)) throw new IOException($"Ordner „{d}“ ist eine Verknüpfung, Verschieben abgebrochen.");
        }
    }

    public bool Exists(string path)
    {
        var p = LongPath(path);
        return File.Exists(p) || Directory.Exists(p);
    }

    // Every existing ancestor up to the volume root must be a plain folder, and the deepest one must
    // resolve (GetFinalPathNameByHandle) to exactly the literal path. Anything unclear fails closed.
    public bool HasReparsePointInChain(string path, string stopAtRoot)
    {
        var root = Path.GetFullPath(stopAtRoot).TrimEnd('\\');
        var full = Path.GetFullPath(path);
        if (!IsUnderOrSame(full, root)) return true;
        string? deepest = null;
        for (var d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
        {
            if (!Directory.Exists(LongPath(d))) continue;   // missing ones are created by Move, one by one
            deepest ??= d;
            if (IsReparsePoint(d)) return true;
        }
        return deepest is not null && !FinalPathMatches(deepest);
    }

    /// <summary>True when the folder's final (link-resolved) path equals its literal path.</summary>
    internal static bool FinalPathMatches(string dir)
    {
        var final = Native.FinalPathNative.Final(LongPath(dir));
        var literal = Native.FinalPathNative.LongName(LongPath(dir));   // 8.3 names expanded, links not resolved
        return final is not null && literal is not null && PathsMatch(literal, final);
    }

    /// <summary>Compares two paths after dropping \\?\ / \\?\UNC\ prefixes and trailing backslashes.</summary>
    internal static bool PathsMatch(string literal, string final) =>
        string.Equals(StripPrefix(literal), StripPrefix(final), StringComparison.OrdinalIgnoreCase);

    static string StripPrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.TrimEnd('\\');
    }

    internal static bool IsUnderOrSame(string path, string root) =>
        path.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            var p = LongPath(path);
            return (File.Exists(p) || Directory.Exists(p))
                && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // cannot tell: treat as unsafe
        }
    }

    internal static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
