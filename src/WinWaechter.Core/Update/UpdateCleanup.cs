using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Update;

/// <summary>Removes leftover download folders (one per downloaded update) from the Updates folder.</summary>
public static class UpdateCleanup
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

    /// <summary>Deletes subfolders last written before maxAge; reparse points are left alone. Returns the count.</summary>
    public static int PurgeOld(string updatesDir, TimeSpan maxAge)
    {
        if (!Directory.Exists(updatesDir)) return 0;
        var cutoff = DateTime.UtcNow - maxAge;
        var n = 0;
        foreach (var d in new DirectoryInfo(updatesDir).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0 || d.LastWriteTimeUtc >= cutoff) continue;
            try
            {
                d.Delete(true);
                n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn($"Alter Update-Ordner nicht löschbar: {d.FullName}", ex);
            }
        }
        return n;
    }
}
