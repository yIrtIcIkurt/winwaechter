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

    // Inspect must see problems, so inaccessible entries throw instead of being skipped.
    static readonly EnumerationOptions Inspect = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
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

    public IEnumerable<DirectoryInfo> EnumerateTopLevelDirs(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateDirectories("*", TopLevel) : [];

    public bool IsVolumeReady(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).IsReady;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Manual walk: never descends into reparse points; any access problem makes the whole result null.
    public FolderFacts? InspectFolder(string dir)
    {
        try
        {
            var root = new DirectoryInfo(LongPath(dir));
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            long bytes = 0;
            var files = 0;
            var latest = root.LastWriteTimeUtc;
            bool binaries = false, reparse = false;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                foreach (var e in d.EnumerateFileSystemInfos("*", Inspect))
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { reparse = true; continue; }
                    if (e.LastWriteTimeUtc > latest) latest = e.LastWriteTimeUtc;
                    if (e is DirectoryInfo sub) { stack.Push(sub); continue; }
                    var f = (FileInfo)e;
                    bytes += f.Length;
                    files++;
                    if (f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        || f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) binaries = true;
                }
            }
            return new FolderFacts(bytes, files, latest, binaries, reparse);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void MoveDirectory(string src, string dst)
    {
        var s = Path.GetFullPath(src);
        var d = Path.GetFullPath(dst);
        if (IsReparsePoint(s)) throw new IOException($"Ordner „{s}“ ist eine Verknüpfung, Verschieben abgebrochen.");
        if (!string.Equals(Path.GetPathRoot(s), Path.GetPathRoot(d), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Quelle und Ziel liegen auf verschiedenen Laufwerken: {s} -> {d}");
        if (Directory.Exists(LongPath(d)) || File.Exists(LongPath(d))) throw new IOException($"Ziel existiert bereits: {d}");
        var parent = Path.GetDirectoryName(d);
        if (!string.IsNullOrEmpty(parent)) CreateChain(parent);
        Directory.Move(LongPath(s), LongPath(d));
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
