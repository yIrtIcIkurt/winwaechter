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

    /// <summary>Roots (e.g. "C:\") whose volume counts as not ready.</summary>
    public HashSet<string> NotReady { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MoveDirectory on one of these throws IOException (folder in use).</summary>
    public HashSet<string> LockedDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>InspectFolder returns null for these (access denied).</summary>
    public HashSet<string> DeniedDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, MoveDirectory throws it (e.g. ArgumentException for a rejected path).</summary>
    public Exception? ThrowOnMoveDirectory { get; set; }

    public List<(string Src, string Dst)> DirMoveCalls { get; } = [];

    readonly WinFileOps _real = new();

    public bool IsVolumeReady(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal) && !NotReady.Contains(root);
    }

    public IEnumerable<DirectoryInfo> EnumerateTopLevelDirs(string dir) => _real.EnumerateTopLevelDirs(dir).ToList();

    public FolderFacts? InspectFolder(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        if (DeniedDirs.Contains(full) || Junctions.Contains(full)) return null;
        var facts = _real.InspectFolder(dir);
        // a faked junction anywhere below counts as a reparse point
        if (facts is not null && !facts.HasReparsePoints
            && Junctions.Any(j => j.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)))
            facts = facts with { HasReparsePoints = true };
        return facts;
    }

    public void MoveDirectory(string src, string dst)
    {
        DirMoveCalls.Add((src, dst));
        if (ThrowOnMoveDirectory is { } boom) throw boom;
        var full = Path.GetFullPath(src).TrimEnd('\\');
        if (LockedDirs.Contains(full)) throw new IOException($"Ordner in Benutzung: {src}");
        if (Junctions.Contains(full)) throw new IOException($"Ordner ist eine Verknüpfung: {src}");
        _real.MoveDirectory(src, dst);
    }

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
        // like the real one: every ancestor up to the volume root counts, not only those below stopAtRoot
        for (var d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
            if (Junctions.Contains(d.TrimEnd('\\'))) return true;
        return false;
    }
}
