using System.Text.RegularExpressions;

namespace WinWaechter.Core.Sys;

public static class PathNormalizer
{
    /// <summary>
    /// Turns service/driver style paths into a full Win32 path: strips \??\, maps \SystemRoot\ and
    /// relative System32\ to %SystemRoot%, expands env vars, resolves ".." when rooted. Never throws.
    /// </summary>
    public static string NormalizePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            var p = path.Trim().Trim('"');
            if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
            if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = @"%SystemRoot%\" + p[12..];
            else if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = @"%SystemRoot%\" + p[11..];
            else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)
                     || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
                p = @"%SystemRoot%\" + p;
            p = Environment.ExpandEnvironmentVariables(p);
            return Path.IsPathRooted(p) ? Path.GetFullPath(p) : p;
        }
        catch { return path; }
    }

    static readonly Regex UserVar = new(@"%(USERPROFILE|LOCALAPPDATA|APPDATA|TEMP|TMP|HOMEPATH|HOMEDRIVE)%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the path names a per-user variable such as %LOCALAPPDATA%.</summary>
    public static bool HasUserVariable(string? path) => !string.IsNullOrEmpty(path) && UserVar.IsMatch(path);

    /// <summary>
    /// Like NormalizePath, but per-user variables come from the interactive user's profile instead of
    /// the (possibly different) elevated process user. Never throws.
    /// </summary>
    public static string NormalizeForUser(string path, IUserContext user) =>
        string.IsNullOrWhiteSpace(path) ? path : NormalizePath(ExpandUserVariables(path, user));

    /// <summary>
    /// Replaces only the per-user variables (%USERPROFILE%, %LOCALAPPDATA%, %APPDATA%, %TEMP%, %TMP%,
    /// %HOMEDRIVE%, %HOMEPATH%) with the interactive user's folders; works on whole command lines. Never throws.
    /// </summary>
    public static string ExpandUserVariables(string text, IUserContext user)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return text;
            var profile = user.ProfileDir.TrimEnd('\\');
            var drive = Path.GetPathRoot(profile)?.TrimEnd('\\') ?? "";
            return UserVar.Replace(text, m => m.Groups[1].Value.ToUpperInvariant() switch
            {
                "USERPROFILE" => profile,
                "LOCALAPPDATA" => Path.Combine(profile, "AppData", "Local"),
                "APPDATA" => Path.Combine(profile, "AppData", "Roaming"),
                "TEMP" or "TMP" => user.TempDir.TrimEnd('\\'),
                "HOMEDRIVE" => drive,
                _ => profile[drive.Length..], // HOMEPATH
            });
        }
        catch { return text; }
    }
}
