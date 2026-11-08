using WinWaechter.Core.Sys;

namespace WinWaechter.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Quoted_path_with_args() =>
        Assert.Equal(@"C:\Program Files\X\x.exe", CommandLineParser.ExtractExePath(@"""C:\Program Files\X\x.exe"" --min"));

    [Fact]
    public void Env_vars_are_expanded()
    {
        var expected = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\X\x.exe");
        Assert.Equal(expected, CommandLineParser.ExtractExePath(@"%LOCALAPPDATA%\X\x.exe /bg"));
    }

    [Fact]
    public void Rundll32_returns_the_dll() =>
        Assert.Equal(@"C:\a\b.dll", CommandLineParser.ExtractExePath(@"rundll32.exe C:\a\b.dll,Entry"));

    [Fact]
    public void Regsvr32_with_switch_returns_the_dll() =>
        Assert.Equal(@"C:\a\b.dll", CommandLineParser.ExtractExePath(@"regsvr32.exe /s ""C:\a\b.dll"""));

    [Fact]
    public void Unquoted_path_without_spaces() =>
        Assert.Equal(@"C:\Tools\app.exe", CommandLineParser.ExtractExePath(@"C:\Tools\app.exe -s"));

    [Fact]
    public void Empty_is_null()
    {
        Assert.Null(CommandLineParser.ExtractExePath(""));
        Assert.Null(CommandLineParser.ExtractExePath("   "));
    }

    [Fact]
    public void Unquoted_path_with_spaces_prefers_existing_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ww dir " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "my app.exe");
        File.WriteAllText(exe, "x");
        try { Assert.Equal(exe, CommandLineParser.ExtractExePath(exe + " /silent")); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unc_path_is_not_probed() =>
        Assert.Equal(@"\\srv\share\a b\app.exe", CommandLineParser.ExtractExePath(@"\\srv\share\a b\app.exe -x"));

    [Fact]
    public void Unquoted_nonexistent_path_cuts_at_first_exe() =>
        Assert.Equal(@"C:\No Such Dir\app.exe", CommandLineParser.ExtractExePath(@"C:\No Such Dir\app.exe -x"));
}
