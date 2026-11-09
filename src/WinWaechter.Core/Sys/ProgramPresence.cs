using System.Security.Principal;

namespace WinWaechter.Core.Sys;

public enum Presence { Present, Missing, Unknown }

public static class ProgramPresence
{
    static readonly Lazy<string?> ProcessSid = new(() =>
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; } catch { return null; }
    });

    /// <summary>
    /// Missing only when the volume is ready and the file is really gone; anything unclear is Unknown.
    /// With a user, per-user variables expand to that user's profile, and a path in another user's
    /// profile is Unknown while the process runs as a different account (it may not see that file).
    /// </summary>
    public static Presence Check(string? path, IFileOps files, IUserContext? user = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Presence.Unknown;
        var p = user is null ? PathNormalizer.NormalizePath(path) : PathNormalizer.NormalizeForUser(path, user);
        if (string.IsNullOrWhiteSpace(p) || p.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathRooted(p)
            || p.Contains('%'))
            return Presence.Unknown;
        if (user is not null && InForeignProfile(p, user)) return Presence.Unknown;
        if (!files.IsVolumeReady(p)) return Presence.Unknown;
        return files.Exists(p) ? Presence.Present : Presence.Missing;
    }

    // Under the profiles folder (e.g. C:\Users) but not in the interactive user's own profile.
    static bool InForeignProfile(string p, IUserContext user)
    {
        if (string.Equals(ProcessSid.Value, user.Sid, StringComparison.OrdinalIgnoreCase)) return false;
        var profile = user.ProfileDir.TrimEnd('\\');
        var profiles = Path.GetDirectoryName(profile);
        if (string.IsNullOrEmpty(profiles)) return false;
        return p.StartsWith(profiles.TrimEnd('\\') +"\\", StringComparison.OrdinalIgnoreCase)
            && !p.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
