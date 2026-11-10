using Microsoft.Win32;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class AppDisplayNamesTests
{
    const string Pk = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    static FakeRegistry Reg(string full, string display) =>
        new FakeRegistry().Seed(Hive.CurrentUser, Pk + "\\" + full, "DisplayName", display);

    [Fact]
    public void Plain_display_name_matches_prefix_and_suffix()
    {
        var reg = Reg("Contoso.App_1.0.0.0_x64__abc123", "Beispiel")
            .Seed(Hive.CurrentUser, Pk + "\\Contoso.App_1.0.0.0_x64__other", "DisplayName", "Falsch");
        Assert.Equal("Beispiel", new WinAppDisplayNames(reg, _ => null).Resolve("Contoso.App_abc123"));
    }

    [Fact]
    public void Short_ms_resource_is_expanded_and_resolved()
    {
        var reg = Reg("Contoso.App_1.0.0.0_x64__abc123", "ms-resource:AppName");
        string? seen = null;
        var n = new WinAppDisplayNames(reg, s => { seen = s; return "Aufgelöst"; }).Resolve("Contoso.App_abc123");
        Assert.Equal("Aufgelöst", n);
        Assert.Equal("@{Contoso.App_1.0.0.0_x64__abc123?ms-resource://Contoso.App/resources/AppName}", seen);
    }

    [Fact]
    public void Full_ms_resource_and_indirect_forms()
    {
        var reg = Reg("Contoso.App_1.0.0.0_x64__abc123", "ms-resource://Contoso.App/resources/Name");
        string? seen = null;
        new WinAppDisplayNames(reg, s => { seen = s; return "X"; }).Resolve("Contoso.App_abc123");
        Assert.Equal("@{Contoso.App_1.0.0.0_x64__abc123?ms-resource://Contoso.App/resources/Name}", seen);

        var reg2 = Reg("Contoso.App_1.0.0.0_x64__abc123", "@{pkg?ms-resource://a/b}");
        new WinAppDisplayNames(reg2, s => { seen = s; return "Y"; }).Resolve("Contoso.App_abc123");
        Assert.Equal("@{pkg?ms-resource://a/b}", seen);
    }

    [Fact]
    public void Falls_back_to_name_before_first_underscore()
    {
        var names = new WinAppDisplayNames(Reg("Contoso.App_1.0_x64__abc123", "ms-resource:N"), _ => null);
        Assert.Equal("Contoso.App", names.Resolve("Contoso.App_abc123"));
        Assert.Equal("Other.App", names.Resolve("Other.App_zzz"));
        // TryResolve does not guess: an unresolved name is reported as such
        Assert.False(names.TryResolve("Contoso.App_abc123", out _));
        Assert.False(names.TryResolve("Other.App_zzz", out _));
        Assert.True(new WinAppDisplayNames(Reg("Contoso.App_1.0_x64__abc123", "Beispiel"), _ => null)
            .TryResolve("Contoso.App_abc123", out var found));
        Assert.Equal("Beispiel", found);
    }

    [Fact]
    public void Package_list_is_read_once_and_enumeration_failure_does_not_throw()
    {
        var reg = Reg("A_1_x64__p", "Eins").Seed(Hive.CurrentUser, Pk + "\\B_1_x64__p", "DisplayName", "Zwei");
        var names = new WinAppDisplayNames(reg, _ => null);
        Assert.Equal("Eins", names.Resolve("A_p"));
        Assert.Equal("Zwei", names.Resolve("B_p"));

        var bad = new FakeRegistry();
        bad.ThrowingKeys.Add(Pk);
        var n2 = new WinAppDisplayNames(bad, _ => null);
        Assert.Equal("A", n2.Resolve("A_p"));
        Assert.Equal("B", n2.Resolve("B_p"));
    }
}
