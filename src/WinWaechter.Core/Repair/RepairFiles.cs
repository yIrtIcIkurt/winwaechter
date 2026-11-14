namespace WinWaechter.Core.Repair;

/// <summary>The few file operations the repair tools need (folders by name, files by pattern), behind a fake.</summary>
public interface IRepairFiles
{
    bool DirectoryExists(string dir);

    /// <summary>Direct subfolders (full paths); empty when dir is missing.</summary>
    IEnumerable<string> Directories(string dir);

    /// <summary>Files directly in dir matching a simple * pattern (full paths); empty when dir is missing.</summary>
    IEnumerable<string> Files(string dir, string pattern);

    void DeleteFile(string path);

    /// <summary>Deletes a folder with everything in it.</summary>
    void DeleteDirectory(string dir);

    void MoveDirectory(string src, string dst);
}

public sealed class WinRepairFiles : IRepairFiles
{
    public bool DirectoryExists(string dir) => Directory.Exists(dir);

    public IEnumerable<string> Directories(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : [];

    public IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList() : [];

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string dir) => Directory.Delete(dir, recursive: true);

    public void MoveDirectory(string src, string dst) => Directory.Move(src, dst);
}
