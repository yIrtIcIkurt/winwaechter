using WinWaechter.Core.Rules;

namespace WinWaechter.Tests;

public sealed class RuleSetTests
{
    static readonly RuleSet Rules = RuleSet.Default;

    [Fact]
    public void Protected_beats_expendable() =>
        Assert.Equal(RuleVerdict.Protected, Rules.Classify(new ItemFacts("Dell Update", null, "Dell Inc", null)));

    [Fact]
    public void Lexware_is_protected() =>
        Assert.Equal(RuleVerdict.Protected,
            Rules.Classify(new ItemFacts("Buchhaltung", @"C:\Program Files (x86)\Lexware\x.exe", null, null)));

    [Fact]
    public void Steam_is_expendable() =>
        Assert.Equal(RuleVerdict.Expendable, Rules.Classify(new ItemFacts("Steam", @"C:\Games\Steam\steam.exe", "Valve", null)));

    [Fact]
    public void Glary_is_pua() =>
        Assert.Equal(RuleVerdict.Pua, Rules.Classify(new ItemFacts("Glary Utilities", null, null, null)));

    [Fact]
    public void Xmrig_is_miner() =>
        Assert.Equal(RuleVerdict.Miner, Rules.Classify(new ItemFacts("svc", @"C:\Users\x\AppData\xmrig.exe", null, null)));

    [Fact]
    public void Unknown_is_unknown() =>
        Assert.Equal(RuleVerdict.Unknown, Rules.Classify(new ItemFacts("Modeller", @"C:\Apps\foo.exe", "Acme", null)));

    [Fact]
    public void Miner_cmdline_detected()
    {
        Assert.True(Rules.IsMinerCommandLine("-o stratum+tcp://pool:3333"));
        Assert.False(Rules.IsMinerCommandLine("notepad.exe readme.txt"));
    }

    [Fact]
    public void Short_names_match_on_word_start_only() =>
        Assert.NotEqual(RuleVerdict.Protected, Rules.Classify(new ItemFacts("Modeller", null, null, null)));

    [Fact]
    public void Dell_service_name_is_protected() =>
        Assert.Equal(RuleVerdict.Protected, Rules.Classify(new ItemFacts("DellClientManagementService", null, null, null)));

    [Fact]
    public void Windows_path_is_protected() =>
        Assert.Equal(RuleVerdict.Protected, Rules.Classify(new ItemFacts("x", @"C:\Windows\System32\x.exe", null, null)));

    [Fact]
    public void Pool_ports_loaded() => Assert.Contains(3333, Rules.PoolPorts);

    [Fact]
    public void Load_throws_on_missing_or_malformed_file()
    {
        Assert.ThrowsAny<Exception>(() => RuleSet.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json")));
        var bad = Path.GetTempFileName();
        try
        {
            File.WriteAllText(bad, "{ not json");
            Assert.ThrowsAny<Exception>(() => RuleSet.Load(bad));
        }
        finally { File.Delete(bad); }
    }

    static RuleVerdict C(string name, string? exe = null, string? pub = null) =>
        Rules.Classify(new ItemFacts(name, exe, pub, null));

    [Fact]
    public void SystemRoot_driver_path_is_protected() =>
        Assert.Equal(RuleVerdict.Protected, C("drv", @"\SystemRoot\System32\drivers\x.sys"));

    [Fact]
    public void Relative_system32_path_is_protected() =>
        Assert.Equal(RuleVerdict.Protected, C("drv", @"System32\drivers\x.sys"));

    [Fact]
    public void Spoofed_windows_folder_in_profile_is_not_protected() =>
        Assert.Equal(RuleVerdict.Miner, C("svc", @"C:\Users\x\Windows\xmrig.exe"));

    [Fact]
    public void Realtek_under_program_files_without_publisher_is_protected() =>
        Assert.Equal(RuleVerdict.Protected, C("RtkNGUI", @"C:\Program Files\Realtek\Audio\RtkNGUI64.exe"));

    [Fact]
    public void Realtek_folder_in_appdata_is_not_protected() =>
        Assert.Equal(RuleVerdict.Unknown, C("foo", @"C:\Users\x\AppData\Local\Realtek\foo.exe"));

    [Fact]
    public void Intel_publisher_protected() => Assert.Equal(RuleVerdict.Protected, C("foo", null, "Intel Corporation"));

    [Fact]
    public void Haufe_publisher_protected() => Assert.Equal(RuleVerdict.Protected, C("foo", null, "Haufe Group"));

    [Fact]
    public void Kaspersky_name_protected() => Assert.Equal(RuleVerdict.Protected, C("Kaspersky Anti-Virus"));

    [Fact]
    public void Teams_without_publisher_expendable_but_with_microsoft_publisher_protected()
    {
        Assert.Equal(RuleVerdict.Expendable, C("MicrosoftTeams"));
        Assert.Equal(RuleVerdict.Protected, C("MicrosoftTeams", null, "Microsoft Corporation"));
    }

    [Fact]
    public void Empty_protected_list_throws_InvalidDataException()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "{ \"protected\": [] }");
            Assert.Throws<InvalidDataException>(() => RuleSet.Load(f));
        }
        finally { File.Delete(f); }
    }
}
