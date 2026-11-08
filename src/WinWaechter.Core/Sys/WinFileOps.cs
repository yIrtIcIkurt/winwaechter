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

    public IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age)
    {
        if (!Directory.Exists(dir)) return [];
        var cutoff = DateTime.UtcNow - age;
        return new DirectoryInfo(dir).EnumerateFiles("*", Recursive).Where(f => f.LastWriteTimeUtc < cutoff);
    }

    public void Move(string src, string dst)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(dst));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(LongPath(parent));
        File.Move(LongPath(src), LongPath(dst), overwrite: false);
    }

    public bool Exists(string path)
    {
        var p = LongPath(path);
        return File.Exists(p) || Directory.Exists(p);
    }

    internal static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
