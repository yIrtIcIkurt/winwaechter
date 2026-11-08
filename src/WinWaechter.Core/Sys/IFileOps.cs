namespace WinWaechter.Core.Sys;

public interface IFileOps
{
    IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age);

    /// <summary>Files directly in dir (no subfolders, no age filter); empty when dir is missing.</summary>
    IEnumerable<FileInfo> EnumerateTopLevel(string dir);

    void Move(string src, string dst);
    bool Exists(string path);

    /// <summary>
    /// True when stopAtRoot itself or any existing directory between it and path's parent is a
    /// reparse point (junction, symlink …), or when path is not under stopAtRoot at all.
    /// </summary>
    bool HasReparsePointInChain(string path, string stopAtRoot);
}
