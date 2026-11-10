using System.Text.RegularExpressions;

namespace WinWaechter.Core.Programs;

/// <summary>A fixed program plus its argument string; started without a shell.</summary>
public sealed record UninstallCommand(string Exe, string Arguments);

public static partial class UninstallCommandParser
{
    /// <summary>Hosts that run arbitrary code or scripts; never started by WinWaechter.</summary>
    public static readonly IReadOnlySet<string> RefusedHosts = new HashSet<string>(
        ["cmd.exe", "rundll32.exe", "regsvr32.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe"],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidRegex();

    // Exactly msiexec(.exe) (optionally quoted / with a path), /I or /X and the product code; nothing else.
    [GeneratedRegex(@"^""?(?:[^""]*\\)?msiexec(?:\.exe)?""?\s+/[IX]\s*(\{[^}\s]*\})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MsiRegex();

    /// <summary>Splits an UninstallString into program and arguments; null when no rooted program is found.</summary>
    public static UninstallCommand? Parse(string? commandLine, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(commandLine).Trim();
        if (cmd.Length == 0) return null;

        string exe, rest;
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            exe = cmd[1..end];
            rest = cmd[(end + 1)..];
        }
        else
        {
            var pick = UnquotedEnd(cmd, exists);
            if (pick < 0) return null;
            exe = cmd[..pick];
            rest = cmd[pick..];
        }

        exe = exe.Trim();
        if (!IsLocalRooted(exe)) return null;
        return new UninstallCommand(exe, rest.Trim());
    }

    // Unquoted: longest prefix ending in ".exe" (then blank/end) that exists, else the first such prefix.
    static int UnquotedEnd(string cmd, Func<string, bool> exists)
    {
        var ends = new List<int>();
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var e = i + 4;
            if (e == cmd.Length || char.IsWhiteSpace(cmd[e])) ends.Add(e);
            i = cmd.IndexOf(".exe", i + 1, StringComparison.OrdinalIgnoreCase);
        }
        if (ends.Count == 0) return -1;
        // Only probe the file system for local absolute paths (no network, no relative lookups).
        for (var k = ends.Count - 1; k >= 0; k--)
        {
            var candidate = cmd[..ends[k]];
            if (IsLocalRooted(candidate) && SafeExists(exists, candidate)) return ends[k];
        }
        return ends[0];
    }

    static readonly char[] BadPathChars = [':', '*', '?', '"', '<', '>', '|'];

    // Fully qualified drive path (C:\...): no UNC/device paths, no streams (x.txt:y.exe), no wildcards.
    static bool IsLocalRooted(string p) =>
        p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\'
        && p.IndexOfAny(BadPathChars, 2) < 0 && p.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    static bool SafeExists(Func<string, bool> exists, string p)
    {
        try { return exists(p); } catch { return false; }
    }

    /// <summary>The MSI product code of an entry, or null when none can be verified.</summary>
    public static string? MsiProductCode(InstalledProgram p)
    {
        if (p.WindowsInstaller && GuidRegex().IsMatch(p.KeyName)) return p.KeyName;
        if (string.IsNullOrWhiteSpace(p.UninstallString)) return null;
        var m = MsiRegex().Match(p.UninstallString.Trim());
        if (!m.Success) return null;
        var guid = m.Groups[1].Value;
        return GuidRegex().IsMatch(guid) ? guid : null;
    }
}
