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
}
