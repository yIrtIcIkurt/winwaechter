using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Works on real files (point it at a test temp dir) but records moves and can simulate
// locked or access-denied files. FileInfo needs real files, so this is not purely in-memory.
public sealed class FakeFileOps : IFileOps
{
    public List<(string Src, string Dst)> MoveCalls { get; } = [];
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age)
    {
        if (!Directory.Exists(dir)) return [];
        var cutoff = DateTime.UtcNow - age;
        return new DirectoryInfo(dir)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => f.LastWriteTimeUtc < cutoff)
            .ToList();
    }

    public void Move(string src, string dst)
    {
        MoveCalls.Add((src, dst));
        if (Locked.Contains(src)) throw new IOException($"Datei gesperrt: {src}");
        if (Denied.Contains(src)) throw new UnauthorizedAccessException($"Zugriff verweigert: {src}");
        var parent = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Move(src, dst, overwrite: false);
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
