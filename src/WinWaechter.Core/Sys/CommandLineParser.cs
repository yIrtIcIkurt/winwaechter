namespace WinWaechter.Core.Sys;

public static class CommandLineParser
{
    static readonly string[] DllExts = [".dll", ".ocx", ".cpl"];

    /// <summary>Returns the executable (or, for rundll32/regsvr32, the DLL) a command line starts, or null.</summary>
    public static string? ExtractExePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(commandLine).Trim();
        if (cmd.Length == 0) return null;

        string exe, rest;
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            exe = end < 0 ? cmd[1..] : cmd[1..end];
            rest = end < 0 ? "" : cmd[(end + 1)..];
        }
        else (exe, rest) = SplitUnquoted(cmd);

        exe = exe.Trim();
        if (exe.Length == 0) return null;

        var host = Path.GetFileNameWithoutExtension(exe);
        if (host.Equals("rundll32", StringComparison.OrdinalIgnoreCase)
            || host.Equals("regsvr32", StringComparison.OrdinalIgnoreCase))
            return DllFromArgs(rest) ?? exe;
        return exe;
    }

    // Unquoted: the longest prefix ending in ".exe" that exists wins; otherwise cut at the first ".exe".
    static (string exe, string rest) SplitUnquoted(string cmd)
    {
        var ends = new List<int>();
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var e = i + 4;
            if (e == cmd.Length || char.IsWhiteSpace(cmd[e])) ends.Add(e);
            i = cmd.IndexOf(".exe", i + 1, StringComparison.OrdinalIgnoreCase);
        }
        if (ends.Count > 0)
        {
            var existing = ends.Where(e => SafeExists(cmd[..e])).ToList();
            var pick = existing.Count > 0 ? existing.Max() : ends[0];
            return (cmd[..pick], cmd[pick..]);
        }
        var sp = cmd.IndexOfAny([' ', '\t']);
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[sp..]);
    }

    static bool SafeExists(string p)
    {
        try { return File.Exists(p); } catch { return false; }
    }

    // rundll32 "C:\a b\x.dll",Entry  /  regsvr32 /s x.dll
    static string? DllFromArgs(string args)
    {
        var s = args.Trim();
        while (s.StartsWith('/') || s.StartsWith('-'))
        {
            var sp = s.IndexOfAny([' ', '\t']);
            if (sp < 0) return null;
            s = s[sp..].TrimStart();
        }
        s = s.TrimStart('"');
        if (s.Length == 0) return null;

        foreach (var ext in DllExts)
        {
            var i = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return s[..(i + ext.Length)];
        }
        // Unusual extension: first token without ",Entry"
        var tokenEnd = s.IndexOfAny([' ', '\t', ',', '"']);
        return tokenEnd < 0 ? s : s[..tokenEnd];
    }
}
