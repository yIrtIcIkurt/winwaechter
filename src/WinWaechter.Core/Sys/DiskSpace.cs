namespace WinWaechter.Core.Sys;

public sealed record DriveSpace(string Drive, long FreeBytes, long TotalBytes);

public interface IDiskSpace { DriveSpace? SystemDrive(); }

/// <summary>Free space of the Windows drive; null on any error.</summary>
public sealed class WinDiskSpace : IDiskSpace
{
    public DriveSpace? SystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root)) return null;
            var d = new DriveInfo(root);
            return new DriveSpace(d.Name, d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class DiskSpace
{
    const long MinFree = 15L * 1024 * 1024 * 1024;

    /// <summary>Low when under 10 % free or under 15 GiB free.</summary>
    public static bool IsLow(DriveSpace d) => d.FreeBytes < d.TotalBytes / 10 || d.FreeBytes < MinFree;
}
