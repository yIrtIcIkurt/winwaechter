using WinWaechter.Core.Model;

namespace WinWaechter.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Core_assembly_version_matches_props()
    {
        Assert.Equal("0.1.0", typeof(Finding).Assembly.GetName().Version!.ToString(3));
    }
}