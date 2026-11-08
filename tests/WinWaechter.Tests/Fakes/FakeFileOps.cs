using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Works on real files (point it at a test temp dir) but records moves and can simulate
// locked or access-denied files. FileInfo needs real files, so this is not purely in-memory.
public sealed class FakeFileOps : IFileOps
{
    public List<(string Src, string Dst)> MoveCalls { get; } = [];
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Moving one of these throws an unexpected error (simulates a crash midway).</summary>
    public HashSet<string> Crash { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories that count as reparse points (junctions) in HasReparsePointInChain.</summary>
    public HashSet<string> Junctions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age)
    {
        if (!Directory.Exists(dir)) return [];
        var cutoff = DateTime.UtcNow - age;
        return new DirectoryInfo(dir)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => f.LastWriteTimeUtc < cutoff)
            .ToList();
    }

    public IEnumerable<FileInfo> EnumerateTopLevel(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList()
            : [];

    public void Move(string src, string dst)
    {
        MoveCalls.Add((src, dst));
        if (Locked.Contains(src)) throw new IOException($"Datei gesperrt: {src}");
        if (Denied.Contains(src)) throw new UnauthorizedAccessException($"Zugriff verweigert: {src}");
        if (Crash.Contains(src)) throw new InvalidOperationException($"Absturz beim Verschieben: {src}");
        var parent = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Move(src, dst, overwrite: false);
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool HasReparsePointInChain(string path, string stopAtRoot)
    {
        var root = Path.GetFullPath(stopAtRoot).TrimEnd('\\');
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            && !full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        for (var d = Path.GetDirectoryName(full); d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
            if (Junctions.Contains(d.TrimEnd('\\'))) return true;
        return Junctions.Contains(root);
    }
}
