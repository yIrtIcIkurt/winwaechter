using System.Text.Json.Nodes;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class LeftoverTests : IDisposable
{
    const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-leftover-" + Guid.NewGuid().ToString("N"));
    readonly string _pf;
    readonly FakeRegistry _reg = new();
    readonly FakeFileOps _files = new();
    readonly FakeProcessList _procs = new();

    public LeftoverTests()
    {
        _pf = Path.Combine(_root, "pf");
        Directory.CreateDirectory(_pf);
        _reg.Seed(Hive.LocalMachine, Uninstall + @"\{Bar}", "DisplayName", "Bar Studio 2.1");
        _reg.Seed(Hive.LocalMachine, Uninstall + @"\{Bar}", "Publisher", "Baz Ltd");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // Creates pf\<folder>\<file> and backdates every file and folder below pf by 40 days.
    string Make(string folder, string file = "x.dll")
    {
        var dir = Path.Combine(_pf, folder);
        var path = Path.Combine(dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[7]);
        Backdate(_pf, 40);
        return dir;
    }

    static void Backdate(string root, int days)
    {
        var t = DateTime.UtcNow.AddDays(-days);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, t);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(d, t);
    }

    IReadOnlyList<Finding> Scan(RuleSet? rules = null) =>
        new LeftoverFolderScanner(_files, _reg, rules ?? RuleSet.Default, _procs, [_pf]).Scan();

    // Default rules with an empty leftoverIgnore list: only the protected list can block a folder then.
    RuleSet RulesWithoutIgnoreList()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
        if (!File.Exists(src)) src = Path.Combine(AppContext.BaseDirectory, "rules.json");
        var json = JsonNode.Parse(File.ReadAllText(src))!;
        json["leftoverIgnore"] = new JsonArray();
        var path = Path.Combine(_root, "rules-noignore.json");
        File.WriteAllText(path, json.ToJsonString());
        return RuleSet.Load(path);
    }

    [Fact]
    public void Folder_without_program_and_with_binaries_is_offered()
    {
        var dir = Make("Foo", "foo.dll");

        var f = Assert.Single(Scan());
        Assert.Equal($"leftover|{dir}", f.Id);
        Assert.Equal(FindingArea.Speicher, f.Area);
        Assert.Equal("Foo", f.Title);
        Assert.Equal(dir, f.Location);
        Assert.False(f.Preselected);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.Equal(FindingGroups.OrphanFolders, f.Group);
        Assert.Equal("folder", f.Change!.Kind);
        Assert.Equal(dir, f.Change.Target);
        Assert.Equal(7, f.SizeBytes);
        Assert.Equal("Kein installiertes Programm gefunden", f.Reasons[0]);
        Assert.StartsWith("Zuletzt geändert am ", f.Reasons[1]);
    }

    [Fact]
    public void Matching_display_name_publisher_or_install_location_is_kept()
    {
        _reg.Seed(Hive.CurrentUser, Uninstall + @"\Qux", "InstallLocation", @"C:\Apps\Qux\");
        Make("Bar Studio");
        Make("Baz");
        Make("Qux");

        Assert.Empty(Scan());
    }

    [Fact]
    public void Display_icon_uninstall_string_and_wow64_entries_also_count()
    {
        const string wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        _reg.Seed(Hive.LocalMachine, wow + @"\{Ico}", "DisplayIcon", @"C:\Old\Iconic\iconic.exe,0");
        _reg.Seed(Hive.LocalMachine, Uninstall + @"\{Un}", "UninstallString", @"""C:\Old\Remover Kit\unins000.exe"" /S");
        Make("Iconic");
        Make("Remover Kit");

        Assert.Empty(Scan());
    }

    [Fact]
    public void Word_start_match_only()
    {
        _reg.Seed(Hive.LocalMachine, Uninstall + @"\{S}", "DisplayName", "Superfoo");
        Make("foo");
        Assert.Single(Scan());

        _reg.Seed(Hive.LocalMachine, Uninstall + @"\{T}", "DisplayName", "Foo-Tools");
        Assert.Empty(Scan());
    }

    [Fact]
    public void Very_short_folder_names_always_count_as_installed()
    {
        Make("ab");
        Make("x");
        Assert.Empty(Scan());
    }

    [Fact]
    public void Appx_package_name_protects_folder()
    {
        _reg.SeedKey(Hive.CurrentUser, Packages + @"\Contoso.Widget_1.0.0.0_x64__abc");
        Make("Contoso Widget");

        Assert.Empty(Scan());
    }

    [Fact]
    public void Ignored_protected_recent_running_or_without_binaries_are_skipped()
    {
        Make("Common Files");
        Make("Dell");
        var fresh = Make("Fresh");
        var running = Make("Running", "r.exe");
        _procs.List.Add(new RunningProcess("r.exe", Path.Combine(running, "r.exe")));
        Make("DataOnly", "save.dat");
        File.SetLastWriteTimeUtc(Path.Combine(fresh, "x.dll"), DateTime.UtcNow.AddDays(-5));

        Assert.Empty(Scan());
    }

    [Fact]
    public void Running_process_in_a_subfolder_also_keeps_the_folder()
    {
        var dir = Make("Deep", @"bin\x64\tool.exe");
        _procs.List.Add(new RunningProcess("tool.exe", Path.Combine(dir, "bin", "x64", "tool.exe")));
        Make("Other");

        var f = Assert.Single(Scan());
        Assert.Equal("Other", f.Title);
    }

    [Fact]
    public void Unreadable_process_list_means_no_findings()
    {
        Make("Foo");
        _procs.Throw = new InvalidOperationException("kaputt");

        Assert.Empty(Scan());
    }

    [Fact]
    public void Folder_with_inner_junction_or_access_denied_is_skipped()
    {
        var j = Make("J");
        Directory.CreateDirectory(Path.Combine(j, "inner"));
        Backdate(_pf, 40);
        _files.Junctions.Add(Path.Combine(j, "inner"));
        var denied = Make("Denied");
        _files.DeniedDirs.Add(denied);

        Assert.Empty(Scan());
    }

    [Fact]
    public void No_uninstall_entries_means_no_leftover_findings()
    {
        _reg.Delete(Hive.LocalMachine, Uninstall + @"\{Bar}", "DisplayName");
        _reg.Delete(Hive.LocalMachine, Uninstall + @"\{Bar}", "Publisher");
        var empty = new FakeRegistry();
        Make("Foo", "foo.dll");

        Assert.Empty(new LeftoverFolderScanner(_files, empty, RuleSet.Default, _procs, [_pf]).Scan());
        Assert.Equal(0, InstalledProgramIndex.Load(empty).UninstallEntryCount);
        // an entry key without any readable field does not count either
        Assert.Equal(0, InstalledProgramIndex.Load(_reg).UninstallEntryCount);
        Assert.Empty(Scan());
    }

    [Fact]
    public void Lexware_dell_and_microsoft_folders_are_never_offered()
    {
        foreach (var n in new[] { "Lexware", "Lexware buchhalter 2024", "Dell", "Dell SupportAssist",
                     "Microsoft", "Microsoft Office", "Microsoft Visual Studio", "Haufe Lexware" })
            Make(n);
        Make("Foo");

        // with the shipped rules (ignore list + protected list)
        Assert.Equal("Foo", Assert.Single(Scan()).Title);
    }

    [Fact]
    public void Protected_list_alone_blocks_lexware_and_dell_folders()
    {
        foreach (var n in new[] { "Lexware", "Lexware buchhalter 2024", "Dell", "Dell SupportAssist", "Haufe Lexware" })
            Make(n);
        Make("Foo");

        Assert.Equal("Foo", Assert.Single(Scan(RulesWithoutIgnoreList())).Title);
    }

    [Fact]
    public void Protected_list_alone_blocks_the_real_vendor_folders_under_program_files()
    {
        // Same facts the scanner builds (folder name + folder path with trailing backslash), real roots.
        var rules = RulesWithoutIgnoreList();
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        foreach (var root in new[] { pf, pf86 })
            foreach (var n in new[] { "Lexware", "Dell", "Microsoft", "Microsoft Office", "Haufe", "Intel", "Realtek" })
                Assert.Equal(RuleVerdict.Protected, rules.Classify(new ItemFacts(n, Path.Combine(root, n) + "\\", null, null)));
    }

    [Fact]
    public void Default_roots_are_program_files_and_local_programs_only()
    {
        var p = new CleanerPaths(@"C:\U\AppData\Local", @"C:\U\AppData\Roaming", @"C:\Windows", @"C:\ProgramData",
            @"C:\Program Files", @"C:\Program Files (x86)");

        var roots = LeftoverFolderScanner.DefaultRoots(p);
        Assert.Equal([@"C:\Program Files", @"C:\Program Files (x86)", @"C:\U\AppData\Local\Programs"], roots);
        Assert.DoesNotContain(p.ProgramData, roots);
        Assert.DoesNotContain(p.RoamingAppData, roots);
        Assert.DoesNotContain(p.LocalAppData, roots);

        // same folder twice (32-bit layout) is listed once
        Assert.Equal(2, LeftoverFolderScanner.DefaultRoots(p with { ProgramFilesX86 = p.ProgramFiles }).Count);
    }

    [Theory]
    [InlineData("Foo-Tools  2.1", "foo tools 2 1")]
    [InlineData(@"C:\Apps\Qux\", "c apps qux")]
    [InlineData("  Ärger_Café ", "ärger café")]
    public void Normalize_lowercases_and_collapses_separators(string input, string expected) =>
        Assert.Equal(expected, InstalledProgramIndex.Normalize(input));
}
