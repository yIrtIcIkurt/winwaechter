using System.Text;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ProgramLeftoverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-leftover-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeFileOps _files = new();
    readonly FakeProcessList _procs = new();
    readonly FakeTaskStore _store = new();
    readonly CleanerPaths _paths;
    readonly string _pf;

    public ProgramLeftoverTests()
    {
        Directory.CreateDirectory(_root);
        _pf = Path.Combine(_root, "pf");
        _paths = new CleanerPaths(Path.Combine(_root, "AppData", "Local"), Path.Combine(_root, "AppData", "Roaming"),
            Path.Combine(_root, "win"), Path.Combine(_root, "pd"), _pf, Path.Combine(_root, "pf86"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static InstalledProgram Prog(string key = "{FOO}", string? location = null, string? icon = null,
        string name = "Foo 1.0", string? publisher = "Foo GmbH", bool msi = false, ProgramFlag flag = ProgramFlag.None) =>
        new(Hive.LocalMachine, UninstallRoots.Native, key, name, publisher, "1.0", null, null, null, location, icon, msi, flag);

    string FooFolder(string sub = "Foo")
    {
        var dir = Path.Combine(_pf, sub);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "foo.dll"), "x");   // written just now: no age rule applies
        return dir;
    }

    UninstallLeftoverFinder Finder(Func<IReadOnlyList<Finding>>? scan = null, RuleSet? rules = null) =>
        new(_reg, _files, rules ?? RuleSet.Default, _procs, _paths, scan ?? (() => []));

    IChange Auto(string name) => new StartupApprovedChange(_reg, Hive.CurrentUser,
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", name);

    // ---------- UninstallLeftoverFinder ----------

    [Fact]
    public void Folder_from_install_location_offered_not_preselected_without_age_rule()
    {
        var dir = FooFolder();
        var f = Assert.Single(Finder().Find(Prog(location: dir), []));

        Assert.Equal($"leftover|{dir}", f.Id);
        Assert.Equal(FindingArea.Speicher, f.Area);
        Assert.Equal("Foo", f.Title);
        Assert.Equal(dir, f.Location);
        Assert.Equal(["Rest von „Foo 1.0“"], f.Reasons);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Equal("folder", f.Change!.Kind);
        Assert.Equal(dir, f.Change.Target);
        Assert.Equal(1, f.SizeBytes);
        Assert.Equal(FindingGroups.OrphanFolders, f.Group);
    }

    [Fact]
    public void Display_icon_folder_used_when_install_location_missing()
    {
        var dir = FooFolder();
        var f = Assert.Single(Finder().Find(Prog(icon: $"\"{_pf}\\Foo\\foo.exe\",0"), []));
        Assert.Equal(dir, f.Location);

        Assert.Empty(Finder().Find(Prog(icon: @"C:\Windows\Installer\{x}\i.ico"), []));
    }

    [Fact]
    public void Still_listed_program_gives_nothing()
    {
        var dir = FooFolder();
        var p = Prog(location: dir);
        _reg.Seed(Hive.LocalMachine, p.RegistryKey, "DisplayName", p.DisplayName);
        var trace = new Finding("t", FindingArea.Autostart, "Foo", "loc", ["r"], Rating.Info, false, Auto("Foo"),
            ExePath: dir + @"\tray.exe");
        Assert.Empty(Finder(() => [trace]).Find(p, []));
    }

    [Fact]
    public void Protected_running_shared_or_junction_folders_are_not_offered()
    {
        // protected folder (rule anchored to the test's program files folder)
        var dell = FooFolder(@"Dell\X");
        var rules = RuleSet.Parse(Encoding.UTF8.GetBytes("""{ "protected": [ { "path": "\\Dell\\" } ] }"""));
        Assert.Empty(Finder(rules: rules).Find(Prog(location: dell), []));

        var dir = FooFolder();
        var p = Prog(location: dir);
        Assert.Single(Finder().Find(p, []));   // control: offered when nothing speaks against it

        // a program from the folder is running
        _procs.List.Add(new RunningProcess("a.exe", dir + @"\a.exe"));
        Assert.Empty(Finder().Find(p, []));
        _procs.List.Clear();

        // the process list cannot be read
        _procs.Throw = new InvalidOperationException("boom");
        Assert.Empty(Finder().Find(p, []));
        _procs.Throw = null;

        // another remaining entry uses the folder
        Assert.Empty(Finder().Find(p, [Prog("{OTHER}", location: dir, name: "Other")]));

        // junction inside, or folder not readable
        _files.Junctions.Add(dir + @"\link");
        Assert.Empty(Finder().Find(p, []));
        _files.Junctions.Clear();
        _files.DeniedDirs.Add(dir);
        Assert.Empty(Finder().Find(p, []));
    }

    [Fact]
    public void Traces_in_folder_and_orphans_by_name()
    {
        var dir = Path.Combine(_pf, "FooTool");   // folder already gone, traces still point into it
        var tray = new Finding("auto|tray", FindingArea.Autostart, "Tray", "Run", ["r"], Rating.Info, true, Auto("Tray"),
            ExePath: dir + @"\tray.exe");
        var task = new Finding("task|upd", FindingArea.Aufgabe, "FooTool Updater", @"\FooTool Updater", ["r"], Rating.Info, false,
            new TaskDisableChange(_store, @"\FooTool Updater"), Group: FindingGroups.OrphanEntries,
            ExePath: Path.Combine(_root, "gone", "upd.exe"));
        var unrelated = new Finding("auto|bar", FindingArea.Autostart, "Bar", "Run", ["r"], Rating.Info, false, Auto("Bar"),
            ExePath: _pf + @"\Bar\bar.exe");

        var found = Finder(() => [tray, task, unrelated, tray]).Find(Prog(location: dir, name: "FooTool 1.0"), []);
        Assert.Equal(["auto|tray", "task|upd"], found.Select(f => f.Id));
        Assert.False(found[0].Preselected);
        Assert.True(found[1].Preselected);
    }

    [Fact]
    public void Running_folder_gives_no_folder_finding_but_its_traces_unselected()
    {
        var dir = FooFolder();
        _procs.List.Add(new RunningProcess("foo.exe", dir + @"\foo.exe"));
        var tray = new Finding("auto|tray", FindingArea.Autostart, "Tray", "Run", ["r"], Rating.Info, true, Auto("Tray"),
            ExePath: dir + @"\tray.exe");

        var found = Finder(() => [tray]).Find(Prog(location: dir), []);
        var f = Assert.Single(found);
        Assert.Equal("auto|tray", f.Id);
        Assert.False(f.Preselected);
    }

    [Fact]
    public void Failing_trace_scan_keeps_the_folder_finding()
    {
        var dir = FooFolder();
        var found = Finder(() => throw new InvalidOperationException("WMI")).Find(Prog(location: dir), []);
        Assert.Equal($"leftover|{dir}", Assert.Single(found).Id);
    }

    // ---------- ProgramService ----------

    ProgramService Service(FakeUninstallLauncher? launcher = null, FakeSignatures? sig = null) =>
        new(new ProgramInventory(_reg, RuleSet.Default), _reg, _files, sig ?? new FakeSignatures(),
            launcher ?? new FakeUninstallLauncher(), Finder(), new ForcedRemovalPlanner(_reg, _files, RuleSet.Default, _procs,
                _paths, () => []), _paths);

    [Fact]
    public async Task ProgramService_runs_only_launch_plans()
    {
        var launcher = new FakeUninstallLauncher { ExitCode = 3010 };
        var svc = Service(launcher);
        var settings = new UninstallPlan(UninstallRoute.OpenSettings, null, "x", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunUninstallerAsync(settings));
        Assert.Empty(launcher.Calls);

        var cmd = new UninstallCommand(_pf + @"\Foo\unins.exe", "/x");
        var launch = new UninstallPlan(UninstallRoute.Launch, cmd, "", false);
        var outcome = await svc.RunUninstallerAsync(launch);
        Assert.Equal(3010, outcome.ExitCode);
        Assert.True(outcome.RestartRequired);
        Assert.Equal([cmd], launcher.Calls);
    }

    [Fact]
    public void ProgramService_signature_never_throws()
    {
        var sig = new FakeSignatures { Throw = new IOException("gesperrt") };
        var svc = Service(sig: sig);
        var plan = new UninstallPlan(UninstallRoute.Launch, new UninstallCommand(_pf + @"\Foo\unins.exe", ""), "", false);
        Assert.Equal(new SignatureInfo(SignatureState.Unknown, null, false), svc.UninstallerSignature(plan));
        Assert.Null(svc.UninstallerSignature(new UninstallPlan(UninstallRoute.OpenSettings, null, "x", false)));
    }

    [Fact]
    public void ProgramService_uses_hidden_entries_as_overlap_set()
    {
        var dir = FooFolder();
        var removed = Prog(location: dir);
        var svc = Service();
        Assert.Single(svc.FindLeftovers(removed));

        // a hidden component entry (SystemComponent=1) still lives in the folder
        var hidden = UninstallRoots.Native + @"\{PART}";
        _reg.Seed(Hive.LocalMachine, hidden, "DisplayName", "Foo Runtime");
        _reg.Seed(Hive.LocalMachine, hidden, "SystemComponent", 1, RegistryValueKind.DWord);
        _reg.Seed(Hive.LocalMachine, hidden, "InstallLocation", dir + @"\Runtime");
        Assert.Empty(svc.ReadAll());
        Assert.Empty(svc.FindLeftovers(removed));

        // forced removal sees it as well
        var listed = Prog("{BAR}", location: dir, name: "Foo Main");
        _reg.Seed(Hive.LocalMachine, listed.RegistryKey, "DisplayName", listed.DisplayName);
        var plan = svc.PlanForcedRemoval(listed);
        Assert.Null(plan.Folder);
        Assert.Contains(ForcedRemovalPlanner.FolderPrefix + ProgramFolderRules.UsedByOther, plan.Notes);
        Assert.True(svc.IsStillListed(listed));
        Assert.False(svc.IsStillListed(removed));
    }
}
