using System.Text.RegularExpressions;
using WinWaechter.Core.Model;

namespace WinWaechter.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Core_assembly_version_matches_props()
    {
        // Directory.Build.props is the single version source (the release script sets it).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var props = File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props"));
        var expected = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;

        Assert.Equal(expected, typeof(Finding).Assembly.GetName().Version!.ToString(3));
    }
}
