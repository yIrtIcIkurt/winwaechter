namespace WinWaechter.Core.Sys;

public interface IFileOps
{
    IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age);
    void Move(string src, string dst);
    bool Exists(string path);
}
