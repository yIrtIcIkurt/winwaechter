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

    /// <summary>False for UNC paths, unready drives and on any error.</summary>
    bool IsVolumeReady(string path);

    /// <summary>Direct subfolders of dir, reparse-point folders included; empty when dir is missing.</summary>
    IEnumerable<DirectoryInfo> EnumerateTopLevelDirs(string dir);

    /// <summary>Size/age facts of a folder tree; null when missing, itself a reparse point, or any access is denied.</summary>
    FolderFacts? InspectFolder(string dir);

    /// <summary>
    /// One Directory.Move. IOException when src is a reparse point, dst exists or the volume roots differ.
    /// Creates the parent chain of dst.
    /// </summary>
    void MoveDirectory(string src, string dst);
}

public sealed record FolderFacts(long Bytes, int Files, DateTime LatestWriteUtc, bool HasBinaries, bool HasReparsePoints);
