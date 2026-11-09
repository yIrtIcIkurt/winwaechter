namespace WinWaechter.Core.Sys;

public enum Presence { Present, Missing, Unknown }

public static class ProgramPresence
{
    /// <summary>Missing only when the volume is ready and the file is really gone; anything unclear is Unknown.</summary>
    public static Presence Check(string? path, IFileOps files)
    {
        if (string.IsNullOrWhiteSpace(path)) return Presence.Unknown;
        var p = PathNormalizer.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(p) || p.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathRooted(p))
            return Presence.Unknown;
        if (!files.IsVolumeReady(p)) return Presence.Unknown;
        return files.Exists(p) ? Presence.Present : Presence.Missing;
    }
}
