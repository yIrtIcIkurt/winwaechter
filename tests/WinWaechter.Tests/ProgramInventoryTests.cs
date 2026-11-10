using Microsoft.Win32;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ProgramInventoryTests
{
    readonly FakeRegistry _reg = new();

    ProgramInventory Inv() => new(_reg, RuleSet.Default);

    void Entry(Hive h, string root, string name, string displayName, string? publisher = null)
    {
        _reg.Seed(h, root + "\\" + name, "DisplayName", displayName);
        if (publisher is not null) _reg.Seed(h, root + "\\" + name, "Publisher", publisher);
    }

    [Fact]
    public void Reads_visible_entries_from_all_three_roots()
    {
        Entry(Hive.LocalMachine, UninstallRoots.Native, "A", "Alpha");
        Entry(Hive.LocalMachine, UninstallRoots.Wow64, "B", "Beta");
        Entry(Hive.CurrentUser, UninstallRoots.User, "C", "Gamma");
        var list = Inv().Read();
        Assert.Equal(3, list.Count);
        Assert.Equal(Hive.LocalMachine, list.Single(p => p.DisplayName == "Alpha").Hive);
        Assert.Equal(UninstallRoots.Wow64, list.Single(p => p.DisplayName == "Beta").RootKey);
        var hkcu = list.Single(p => p.DisplayName == "Gamma");
        Assert.Equal(Hive.CurrentUser, hkcu.Hive);
        Assert.StartsWith(@"HKCU\Software\Microsoft", hkcu.Id);
        Assert.False(hkcu.IsMachineEntry);
    }

    [Fact]
    public void Hides_system_components_updates_and_nameless()
    {
        var r = UninstallRoots.Native;
        Entry(Hive.LocalMachine, r, "Sys", "Sys Comp");
        _reg.Seed(Hive.LocalMachine, r + @"\Sys", "SystemComponent", 1, RegistryValueKind.DWord);
        Entry(Hive.LocalMachine, r, "Child", "Child");
        _reg.Seed(Hive.LocalMachine, r + @"\Child", "ParentKeyName", "X");
        _reg.SeedKey(Hive.LocalMachine, r + @"\NoName");
        _reg.Seed(Hive.LocalMachine, r + @"\NoName", "Publisher", "P");
        Entry(Hive.LocalMachine, r, "Upd", "Upd");
        _reg.Seed(Hive.LocalMachine, r + @"\Upd", "ReleaseType", "Security Update");
        Entry(Hive.LocalMachine, r, "Ok", "Normal Program");
        var list = Inv().Read();
        Assert.Equal("Normal Program", Assert.Single(list).DisplayName);
    }

    [Fact]
    public void Parses_date_size_and_msi_flag()
    {
        var r = UninstallRoots.Native;
        Entry(Hive.LocalMachine, r, "A", "A");
        _reg.Seed(Hive.LocalMachine, r + @"\A", "InstallDate", "20240131");
        _reg.Seed(Hive.LocalMachine, r + @"\A", "EstimatedSize", 2048, RegistryValueKind.DWord);
        _reg.Seed(Hive.LocalMachine, r + @"\A", "WindowsInstaller", 1, RegistryValueKind.DWord);
        Entry(Hive.LocalMachine, r, "B", "B");
        _reg.Seed(Hive.LocalMachine, r + @"\B", "InstallDate", "31.01.2024");
        var list = Inv().Read();
        var a = list.Single(p => p.DisplayName == "A");
        Assert.Equal(new DateTime(2024, 1, 31), a.InstallDate);
        Assert.Equal(2_097_152L, a.SizeBytes);
        Assert.True(a.WindowsInstaller);
        var b = list.Single(p => p.DisplayName == "B");
        Assert.Null(b.InstallDate);
        Assert.Null(b.SizeBytes);
        Assert.False(b.WindowsInstaller);
    }

    [Fact]
    public void Flags_from_rules()
    {
        var r = UninstallRoots.Native;
        Entry(Hive.LocalMachine, r, "1", "Some Tool", "Dell Inc.");
        Entry(Hive.LocalMachine, r, "2", "Microsoft Edge", "Microsoft Corporation");
        Entry(Hive.LocalMachine, r, "3", "Buchhaltung");
        _reg.Seed(Hive.LocalMachine, r + @"\3", "DisplayIcon", @"""C:\Program Files\Lexware\x.exe"",0");
        Entry(Hive.LocalMachine, r, "4", "Glary Utilities");
        Entry(Hive.LocalMachine, r, "5", "Foo Tool");
        var list = Inv().Read();
        ProgramFlag F(string n) => list.Single(p => p.DisplayName == n).Flag;
        Assert.Equal(ProgramFlag.Protected, F("Some Tool"));
        Assert.Equal(ProgramFlag.Protected, F("Microsoft Edge"));
        Assert.Equal(ProgramFlag.Protected, F("Buchhaltung"));
        Assert.Equal(ProgramFlag.Pua, F("Glary Utilities"));
        Assert.Equal(ProgramFlag.None, F("Foo Tool"));
    }

    [Fact]
    public void Unreadable_subkeys_are_skipped_and_result_is_sorted()
    {
        var r = UninstallRoots.Native;
        Entry(Hive.LocalMachine, r, "Z", "zeta");
        Entry(Hive.LocalMachine, r, "A", "Alpha");
        Entry(Hive.LocalMachine, r, "Bad", "Bad");
        _reg.ThrowingKeys.Add(r + @"\Bad");
        Assert.Equal(["Alpha", "zeta"], Inv().Read().Select(p => p.DisplayName).ToArray());
    }

    [Fact]
    public void IconExe_strips_quotes_and_index()
    {
        Assert.Equal(@"C:\a b\x.exe", ProgramInventory.IconExe(@"""C:\a b\x.exe"",0"));
        Assert.Equal(@"C:\x.ico", ProgramInventory.IconExe(@"C:\x.ico"));
        Assert.Null(ProgramInventory.IconExe(""));
        Assert.Null(ProgramInventory.IconExe(null));
    }

    [Fact]
    public void IsDirectChild_only_for_children_of_the_three_roots()
    {
        Assert.True(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.Native + @"\{A}"));
        Assert.False(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.Native));
        Assert.False(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.Native + @"\A\B"));
        Assert.False(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.Native + @"\.."));
        Assert.True(UninstallRoots.IsDirectChild(Hive.CurrentUser, UninstallRoots.User + @"\A"));
        Assert.False(UninstallRoots.IsDirectChild(Hive.LocalMachine, @"SOFTWARE\Evil\A"));
    }

    [Fact]
    public void IsDirectChild_is_case_insensitive_and_hive_aware()
    {
        Assert.True(UninstallRoots.IsDirectChild(Hive.CurrentUser, @"software\microsoft\windows\currentversion\uninstall\A"));
        // Native and User are the same registry path apart from case
        Assert.True(UninstallRoots.IsDirectChild(Hive.CurrentUser, UninstallRoots.Native + @"\A"));
        Assert.True(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.User + @"\A"));
        Assert.True(UninstallRoots.IsDirectChild(Hive.LocalMachine, UninstallRoots.Wow64 + @"\A"));
        // the Wow64 root exists only under HKLM
        Assert.False(UninstallRoots.IsDirectChild(Hive.CurrentUser, UninstallRoots.Wow64 + @"\A"));
    }

    [Fact]
    public void IsDirectChild_rejects_grandchildren_for_all_roots()
    {
        foreach (var (hive, root) in UninstallRoots.All)
        {
            Assert.True(UninstallRoots.IsDirectChild(hive, root + @"\A"));
            Assert.False(UninstallRoots.IsDirectChild(hive, root + @"\A\B"));
            Assert.False(UninstallRoots.IsDirectChild(hive, root + @"\"));
        }
    }
}
