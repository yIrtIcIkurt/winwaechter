using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class RegKeyTests : IDisposable
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string Key = UninstallRoots.Native + @"\{A}";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-regkey-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeFileOps _files = new();
    readonly FakeServiceControl _sc = new();
    readonly FakeTaskStore _store = new();
    readonly FakeProcessList _procs = new();
    readonly FakeUserContext _user;
    readonly CleanerPaths _paths;
    readonly string _pf;

    public RegKeyTests()
    {
        Directory.CreateDirectory(_root);
        _user = new FakeUserContext(_root);
        _pf = Path.Combine(_root, "pf");
        _paths = new CleanerPaths(Path.Combine(_root, "AppData", "Local"), Path.Combine(_root, "AppData", "Roaming"),
            Path.Combine(_root, "win"), Path.Combine(_root, "pd"), _pf, Path.Combine(_root, "pf86"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx(Func<string, bool>? trusted = null) => new(Path.Combine(_root, "s"), trusted);

    RegKeyChange Change(string? key = null) => new(_reg, Hive.LocalMachine, key ?? Key, "Alpha");

    void SeedAllKinds()
    {
        _reg.Seed(Hive.LocalMachine, Key, "", "default");
        _reg.Seed(Hive.LocalMachine, Key, "DisplayName", "Alpha");
        _reg.Seed(Hive.LocalMachine, Key, "Path", @"%ProgramFiles%\A", RegistryValueKind.ExpandString);
        _reg.Seed(Hive.LocalMachine, Key, "EstimatedSize", 1234, RegistryValueKind.DWord);
        _reg.Seed(Hive.LocalMachine, Key, "Q", 1L << 40, RegistryValueKind.QWord);
        _reg.Seed(Hive.LocalMachine, Key, "M", new[] { "a", "b" }, RegistryValueKind.MultiString);
        _reg.Seed(Hive.LocalMachine, Key, "B", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        _reg.Seed(Hive.LocalMachine, Key + @"\Sub\Deep", "X", "deep");
    }

    // ---------- RegKeyChange ----------

    [Fact]
    public void Snapshot_round_trips_all_value_kinds_and_subkeys()
    {
        SeedAllKinds();
        var ctx = Ctx();
        var c = Change();
        var plan = c.Plan(ctx);
        var rec = c.Apply(ctx);

        Assert.False(_reg.KeyExists(Hive.LocalMachine, Key));
        Assert.False(_reg.KeyExists(Hive.LocalMachine, Key + @"\Sub\Deep"));
        Assert.Equal("regkey", rec.Kind);
        Assert.Equal(@"HKLM\" + Key, rec.Target);
        Assert.Equal("Deinstallations-Eintrag „Alpha“ entfernen", rec.Description);
        Assert.Equal(plan.After["backup"], rec.After["backup"]);
        Assert.Matches("^[0-9a-f]{64}$", rec.After["sha256"]);
        var backup = rec.After["backup"]!;
        Assert.True(File.Exists(backup));
        Assert.Equal(Path.GetFullPath(ctx.BackupDir), Path.GetDirectoryName(backup));
        Assert.StartsWith("regkey_", Path.GetFileName(backup));

        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        RegKeyChange.FromRecord(rec, _reg).Undo(rec, ctx);

        void Same(string key, string name, object value, RegistryValueKind kind)
        {
            Assert.Equal(kind, _reg.GetKind(Hive.LocalMachine, key, name));
            Assert.Equal(value, _reg.Get(Hive.LocalMachine, key, name));
        }
        Same(Key, "", "default", RegistryValueKind.String);
        Same(Key, "DisplayName", "Alpha", RegistryValueKind.String);
        Same(Key, "Path", @"%ProgramFiles%\A", RegistryValueKind.ExpandString);
        Same(Key, "EstimatedSize", 1234, RegistryValueKind.DWord);
        Same(Key, "Q", 1L << 40, RegistryValueKind.QWord);
        Same(Key, "M", new[] { "a", "b" }, RegistryValueKind.MultiString);
        Same(Key, "B", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        Same(Key + @"\Sub\Deep", "X", "deep", RegistryValueKind.String);
        Assert.Equal(7, _reg.ValueNames(Hive.LocalMachine, Key).Count);
    }

    [Fact]
    public void Apply_refuses_what_it_cannot_restore_and_puts_back_a_partly_deleted_key()
    {
        // deeper than MaxDepth: refused before anything is written
        var deep = Key + string.Concat(Enumerable.Range(0, RegKeyChange.MaxDepth + 1).Select(i => $@"\L{i}"));
        _reg.Seed(Hive.LocalMachine, Key, "DisplayName", "Alpha");
        _reg.Seed(Hive.LocalMachine, deep, "X", "x");
        var ctx = Ctx();
        Assert.Throws<InvalidOperationException>(() => Change().Apply(ctx));
        Assert.Empty(_reg.DeleteTreeCalls);
        Assert.False(Directory.Exists(ctx.BackupDir));

        // a value kind that cannot be written back exactly
        var reg2 = new FakeRegistry();
        reg2.Seed(Hive.LocalMachine, Key, "R", new byte[] { 1 }, RegistryValueKind.Unknown);
        Assert.Throws<InvalidOperationException>(() => new RegKeyChange(reg2, Hive.LocalMachine, Key, "x").Apply(ctx));
        Assert.Empty(reg2.DeleteTreeCalls);

        // delete fails midway: the subkeys come back from the snapshot
        var reg3 = new FakeRegistry { DeleteTreeFailsMidway = new UnauthorizedAccessException("verweigert") };
        reg3.Seed(Hive.LocalMachine, Key, "DisplayName", "Alpha");
        reg3.Seed(Hive.LocalMachine, Key + @"\Sub", "X", "sub");
        var ex = Assert.Throws<InvalidOperationException>(() => new RegKeyChange(reg3, Hive.LocalMachine, Key, "x").Apply(ctx));
        Assert.Contains("zurückgeschrieben", ex.Message);
        Assert.Equal("sub", reg3.Get(Hive.LocalMachine, Key + @"\Sub", "X"));
        Assert.Equal("Alpha", reg3.Get(Hive.LocalMachine, Key, "DisplayName"));
    }

    [Fact]
    public void Undo_of_a_change_that_stopped_before_its_backup_does_nothing()
    {
        var ctx = Ctx();
        var c = Change();
        var pending = c.Plan(ctx);
        Assert.Throws<InvalidOperationException>(() => c.Apply(ctx));   // key missing
        RegKeyChange.FromRecord(pending, _reg).Undo(pending, ctx);
        Assert.Empty(_reg.CreateKeyCalls);
        Assert.Empty(_reg.SetCalls);
    }

    [Fact]
    public void Apply_refuses_value_data_that_does_not_match_its_kind()
    {
        var ctx = Ctx();
        foreach (var (data, kind) in new (object, RegistryValueKind)[]
                 {
                     ("kein-wert", RegistryValueKind.DWord), ("x", RegistryValueKind.Binary),
                     (5, RegistryValueKind.String), (new string?[] { "a", null }, RegistryValueKind.MultiString),
                 })
        {
            var reg = new FakeRegistry();
            reg.Seed(Hive.LocalMachine, Key, "V", data, kind);
            var ex = Assert.Throws<InvalidOperationException>(() => new RegKeyChange(reg, Hive.LocalMachine, Key, "x").Apply(ctx));
            Assert.Contains("kann nicht vollständig gesichert werden", ex.Message);
            Assert.Empty(reg.DeleteTreeCalls);
        }
    }

    [Fact]
    public void Undo_of_backup_without_hash_asks_for_manual_check()
    {
        SeedAllKinds();
        var ctx = Ctx();
        var rec = Change().Apply(ctx);
        var noHash = rec with { After = new() { ["backup"] = rec.After["backup"] } };
        var ex = Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(noHash, _reg).Undo(noHash, ctx));
        Assert.Equal($"Sicherung ohne Prüfsumme – bitte manuell prüfen: {Path.GetFullPath(rec.After["backup"]!)}", ex.Message);
        Assert.False(_reg.KeyExists(Hive.LocalMachine, Key));
        Assert.Empty(_reg.CreateKeyCalls);
    }

    [Fact]
    public void Apply_on_missing_key_writes_nothing()
    {
        var ctx = Ctx();
        var c = Change();
        c.Plan(ctx);
        var ex = Assert.Throws<InvalidOperationException>(() => c.Apply(ctx));
        Assert.Contains("existiert nicht mehr", ex.Message);
        Assert.True(!Directory.Exists(ctx.BackupDir) || Directory.GetFiles(ctx.BackupDir, "regkey_*").Length == 0);
        Assert.Empty(_reg.DeleteTreeCalls);
    }

    [Fact]
    public void Refuses_keys_outside_the_uninstall_roots()
    {
        Assert.Throws<ArgumentException>(() => Change(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        Assert.Throws<ArgumentException>(() => Change(UninstallRoots.Native + @"\A\B"));
        Assert.Throws<ArgumentException>(() => new RegKeyChange(_reg, Hive.CurrentUser, UninstallRoots.Wow64 + @"\A", "x"));

        var evil = new ChangeRecord("1", "regkey", @"HKLM\SOFTWARE\Evil\A", "x",
            new() { ["hive"] = "HKLM", ["key"] = @"SOFTWARE\Evil\A", ["name"] = "x" },
            new() { ["backup"] = "x", ["sha256"] = "y" }, ChangeStatus.Applied, DateTimeOffset.Now);
        Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(evil, _reg));
        var badHive = evil with { Before = new() { ["hive"] = "HKU", ["key"] = Key, ["name"] = "x" } };
        Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(badHive, _reg));
        Assert.Empty(_reg.DeleteTreeCalls);
        Assert.Empty(_reg.CreateKeyCalls);
    }

    [Fact]
    public void IsDirectChild_rejects_null_trailing_backslash_and_sibling_prefix()
    {
        foreach (var (hive, root) in UninstallRoots.All)
        {
            Assert.False(UninstallRoots.IsDirectChild(hive, null!));
            Assert.False(UninstallRoots.IsDirectChild(hive, root + @"\A\"));
            Assert.False(UninstallRoots.IsDirectChild(hive, root + @"X\A"));
        }
    }

    [Fact]
    public void Undo_refuses_tampered_backup_or_untrusted_file()
    {
        SeedAllKinds();
        var ctx = Ctx();
        var rec = Change().Apply(ctx);
        var backup = rec.After["backup"]!;

        var bytes = File.ReadAllBytes(backup);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(backup, bytes);
        Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(rec, _reg).Undo(rec, ctx));
        Assert.False(_reg.KeyExists(Hive.LocalMachine, Key));
        Assert.Empty(_reg.CreateKeyCalls);

        bytes[bytes.Length / 2] ^= 0x01;   // original content again
        File.WriteAllBytes(backup, bytes);
        Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(rec, _reg).Undo(rec, Ctx(_ => false)));
        Assert.False(_reg.KeyExists(Hive.LocalMachine, Key));

        var outside = rec with { After = new(rec.After) { ["backup"] = Path.Combine(_root, Path.GetFileName(backup)) } };
        File.Copy(backup, outside.After["backup"]!);
        Assert.Throws<InvalidOperationException>(() => RegKeyChange.FromRecord(outside, _reg).Undo(outside, ctx));
        Assert.Empty(_reg.CreateKeyCalls);
    }

    [Fact]
    public async Task Undo_refuses_when_key_exists_again_and_drift_is_changed()
    {
        SeedAllKinds();
        var ctx = Ctx();
        var rec = Change().Apply(ctx);
        _reg.Seed(Hive.LocalMachine, Key, "DisplayName", "Neu");

        var c = RegKeyChange.FromRecord(rec, _reg);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
        var ex = Assert.Throws<InvalidOperationException>(() => c.Undo(rec, ctx));
        Assert.Equal("Der Eintrag existiert wieder. Nichts wiederhergestellt.", ex.Message);

        // through the engine: journal + factory
        var reg = new FakeRegistry();
        reg.Seed(Hive.LocalMachine, Key, "DisplayName", "Alpha");
        var factory = ChangeFactory.CreateDefault(reg, _sc, _store, _files, RuleSet.Default);
        var engine = new OptimizerEngine([], new SessionStore(Path.Combine(_root, "sessions")), factory,
            _ => new RestorePointResult(true, "ok"));
        var finding = new Finding("f", FindingArea.Einstellung, "Alpha", Key, ["r"], Rating.Info, true,
            new RegKeyChange(reg, Hive.LocalMachine, Key, "Alpha"));
        var res = await engine.ApplyAsync("T", [finding], null, CancellationToken.None);
        Assert.Equal(1, res.Applied);
        reg.SeedKey(Hive.LocalMachine, Key);
        var u = engine.UndoSession(res.Session);
        Assert.Equal(1, u.SkippedDrift);
        Assert.Equal(0, u.Undone);
    }

    [Fact]
    public void Factory_rehydrates_regkey()
    {
        SeedAllKinds();
        var rec = Change().Apply(Ctx());
        var f = ChangeFactory.CreateDefault(_reg, _sc, _store, _files, RuleSet.Default);
        var c = f.Rehydrate(rec);
        Assert.Equal("regkey", c.Kind);
        Assert.Equal(@"HKLM\" + Key, c.Target);
    }

    // ---------- folder rules ----------

    static InstalledProgram Prog(string key, string? location = null, string? icon = null, string? uninstall = null,
        ProgramFlag flag = ProgramFlag.None, bool msi = false, string name = "Foo Tool") =>
        new(Hive.LocalMachine, UninstallRoots.Native, key, name, "Foo GmbH", "1.0", null, null, uninstall, location, icon,
            msi, flag);

    [Fact]
    public void Folder_rules_normalize_and_refuse_roots()
    {
        // real %ProgramFiles%: the protected list anchors its paths there; nothing touches the disk
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!.TrimEnd('\\');
        var paths = _paths with { ProgramFiles = pf };
        var self = Prog("Self");
        FolderCheck Check(string raw) => ProgramFolderRules.Check(raw, self, [self], paths, RuleSet.Default);

        var quoted = Check($"\"{pf}\\Foo\\\"");
        Assert.Equal(pf + @"\Foo", quoted.Folder);
        Assert.Null(quoted.Refusal);

        var upper = Check(pf.ToUpperInvariant() + @"\foo\");
        Assert.NotNull(upper.Folder);
        Assert.Null(upper.Refusal);

        var env = Check(@"%ProgramFiles%\Foo");
        Assert.Equal(pf + @"\Foo", env.Folder);

        const string Only = "Nur Ordner unter „Programme“ oder „AppData\\Local\\Programs“ werden angeboten.";
        Assert.Equal(new FolderCheck(null, Only), Check(pf + @"\"));
        Assert.Equal(new FolderCheck(null, "Gemeinsam genutzte Ordner werden nie angeboten."), Check(pf + @"\Common Files\X"));
        Assert.Equal(new FolderCheck(null, Only), Check(_user.ProfileDir + @"\AppData\Roaming\Foo"));
        Assert.Equal(new FolderCheck(null, "Der Installationsordner ist keine gültige lokale Pfadangabe."), Check(@"\\nas\x"));
        Assert.Equal(new FolderCheck(null, "Der Ordner ist geschützt."), Check(pf + @"\Dell\X"));
        Assert.Equal(new FolderCheck(null, "Kein Installationsordner eingetragen."), Check("  "));

        // LocalAppData\Programs counts as well, but not LocalAppData itself
        Assert.NotNull(Check(Path.Combine(_paths.LocalAppData, "Programs", "Foo")).Folder);
        Assert.Equal(Only, Check(Path.Combine(_paths.LocalAppData, "Foo")).Refusal);
    }

    [Fact]
    public void Folder_rules_refuse_overlap_with_other_entries()
    {
        var self = Prog("Self");
        FolderCheck Check(string folder, InstalledProgram other) =>
            ProgramFolderRules.Check(folder, self, [self, other], _paths, RuleSet.Default);
        const string Shared = "Ein anderes installiertes Programm nutzt diesen Ordner.";

        Assert.Equal(Shared, Check(_pf + @"\Foo", Prog("O1", location: _pf + @"\Foo\Plugin")).Refusal);
        Assert.Equal(Shared, Check(_pf + @"\Foo\Sub", Prog("O2", location: _pf + @"\Foo")).Refusal);
        Assert.Equal(Shared, Check(_pf + @"\Foo", Prog("O3", icon: $"\"{_pf}\\Foo\\x.exe\",0")).Refusal);
        Assert.Equal(Shared, Check(_pf + @"\Foo", Prog("O4", uninstall: $"\"{_pf}\\FOO\\unins.exe\" /x")).Refusal);
        Assert.Null(Check(_pf + @"\Foo", Prog("O5", location: _pf + @"\Foobar")).Refusal);
        // the entry itself never counts as "another" one
        var selfWithIcon = Prog("Self", icon: _pf + @"\Foo\foo.exe");
        Assert.Null(ProgramFolderRules.Check(_pf + @"\Foo", selfWithIcon, [selfWithIcon], _paths, RuleSet.Default).Refusal);
    }

    // ---------- scanners and traces ----------

    [Fact]
    public void Scanners_fill_exe_path()
    {
        var gone = Path.Combine(_root, "gone");
        var sig = new FakeSignatures();
        _reg.Seed(Hive.CurrentUser, Run, "Foo", $"\"{gone}\\foo.exe\" --min");
        var a = Assert.Single(new AutostartScanner(_reg, _user, sig, RuleSet.Default, _files, new FakeShortcutResolver()).Scan());
        Assert.Equal(gone + @"\foo.exe", a.ExePath);

        _sc.Add("GoneSvc", imagePath: $"\"{gone}\\svc.exe\" -k");
        var s = Assert.Single(new ServiceScanner(_sc, sig, RuleSet.Default, _reg, _files).Scan());
        Assert.Equal(gone + @"\svc.exe", s.ExePath);

        _store.Add(@"\GoneTask", actionExe: gone + @"\t.exe");
        var t = Assert.Single(new TaskScanner(_store, sig, RuleSet.Default, _files, _user).Scan());
        Assert.Equal(gone + @"\t.exe", t.ExePath);
    }

    static Finding F(string id, string? exe, IChange? change, string title = "x", string location = "loc",
        string? group = null) =>
        new(id, FindingArea.Autostart, title, location, ["r"], Rating.Info, false, change, Group: group, ExePath: exe);

    IChange Auto(string name) => new StartupApprovedChange(_reg, Hive.CurrentUser,
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", name);

    [Fact]
    public void Traces_in_folder_and_by_name()
    {
        var a = F("a", _pf + @"\Foo\a.exe", Auto("a"));
        var b = F("b", _pf + @"\Foobar\b.exe", Auto("b"));
        var c = F("c", _pf + @"\Foo\c.exe", null);
        Assert.Equal([a], ProgramTraces.InFolder([a, b, c], _pf + @"\Foo"));

        Assert.Equal("footool", ProgramTraces.NameKey("FooTool 2.3.1"));
        var gone = Path.Combine(_root, "gone") + @"\";   // never created: program files are missing
        var hit = F("h", gone + "up.exe", Auto("h"), title: "FooTool Updater", group: FindingGroups.OrphanEntries);
        var inLocation = F("l", gone + "up2.exe", Auto("l"), location: @"C:\gone\FooTool\up.exe", group: FindingGroups.OrphanEntries);
        var notWordStart = F("n", gone + "n.exe", Auto("n"), title: "SuperFooTool", group: FindingGroups.OrphanEntries);
        var notWordEnd = F("e", gone + "e.exe", Auto("e"), title: "FooToolbox Helper", group: FindingGroups.OrphanEntries);
        var notOrphan = F("o", gone + "o.exe", Auto("o"), title: "FooTool Helper");
        var noChange = F("z", gone + "z.exe", null, title: "FooTool Z", group: FindingGroups.OrphanEntries);
        Assert.Equal([hit, inLocation], ProgramTraces.ByName([hit, inLocation, notWordStart, notWordEnd, notOrphan, noChange],
            "FooTool 2.3.1", _files));
    }

    [Fact]
    public void ByName_never_offers_live_entries_of_other_programs()
    {
        // tagged as orphan but the program file exists (another program sharing the name): not offered
        var live = Path.Combine(_root, "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "tool.exe"), "x");
        var present = F("p", Path.Combine(live, "tool.exe"), Auto("p"), title: "FooTool Sync", group: FindingGroups.OrphanEntries);
        // no or unclear path (relative, unresolved variable): not offered either
        var noPath = F("n", null, Auto("n"), title: "FooTool Sync", group: FindingGroups.OrphanEntries);
        var unclear = F("u", @"%FOO_UNSET_VAR%\tool.exe", Auto("u"), title: "FooTool Sync", group: FindingGroups.OrphanEntries);
        var missing = F("m", Path.Combine(_root, "gone", "tool.exe"), Auto("m"), title: "FooTool Sync",
            group: FindingGroups.OrphanEntries);
        Assert.Equal([missing], ProgramTraces.ByName([present, noPath, unclear, missing], "FooTool", _files));
    }

    [Fact]
    public void ByName_needs_the_whole_name_with_at_least_four_characters()
    {
        var gone = Path.Combine(_root, "gone") + @"\";
        var foo = F("f", gone + "f.exe", Auto("f"), title: "Foo Updater", group: FindingGroups.OrphanEntries);
        Assert.Empty(ProgramTraces.ByName([foo], "Foo 1.0", _files));           // "foo": too short
        Assert.Empty(ProgramTraces.ByName([foo], "Fo", _files));

        var pro = F("p", gone + "p.exe", Auto("p"), title: "Acme Updater", group: FindingGroups.OrphanEntries);
        var whole = F("w", gone + "w.exe", Auto("w"), title: "Acme Photo Pro Updater", group: FindingGroups.OrphanEntries);
        // only the whole (normalized) name counts, not one of its words
        Assert.Equal([whole], ProgramTraces.ByName([pro, whole], "ACME Photo-Pro 2.0", _files));
        Assert.Equal("acme", ProgramTraces.NameKey("Acme"));
        Assert.Equal([pro], ProgramTraces.ByName([pro], "Acme", _files));
    }

    // ---------- forced removal ----------

    [Fact]
    public void IsOffered_rules()
    {
        Assert.False(ForcedRemovalPlanner.IsOffered(Prog("A", flag: ProgramFlag.Protected), true, true));
        Assert.False(ForcedRemovalPlanner.IsOffered(Prog("A", msi: true), true, true));
        Assert.False(ForcedRemovalPlanner.IsOffered(Prog("A"), false, false));
        Assert.True(ForcedRemovalPlanner.IsOffered(Prog("A"), true, false));
        Assert.True(ForcedRemovalPlanner.IsOffered(Prog("A"), false, true));
        Assert.True(ForcedRemovalPlanner.IsOffered(Prog("A", flag: ProgramFlag.Pua), true, false));
    }

    string FooFolder()
    {
        var dir = Path.Combine(_pf, "Foo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "foo.exe"), "x");
        return dir;
    }

    InstalledProgram SeededProg(string? location)
    {
        var p = Prog("{A}", location: location);
        _reg.Seed(Hive.LocalMachine, p.RegistryKey, "DisplayName", p.DisplayName);
        return p;
    }

    ForcedRemovalPlanner Planner(Func<IReadOnlyList<Finding>> scan) =>
        new(_reg, _files, RuleSet.Default, _procs, _paths, scan);

    [Fact]
    public void Forced_plan_lists_traces_folder_then_regkey()
    {
        var dir = FooFolder();
        var p = SeededProg(dir);
        var traces = new List<Finding>
        {
            F("auto", dir + @"\foo.exe", Auto("foo")),
            F("task", dir + @"\t.exe", new TaskDisableChange(_store, @"\FooTask")),
            F("svc", dir + @"\svc.exe", new ServiceStartModeChange(_sc, _reg, "FooSvc")),
            F("other", _pf + @"\Other\o.exe", Auto("other")),
        };

        var plan = Planner(() => traces).Plan(p, [p]);
        Assert.Equal(["service", "task", "autostart", "folder", "regkey"], plan.Changes.Select(c => c.Kind));
        Assert.Equal(dir, plan.Folder);
        Assert.Equal(dir, plan.Changes[3].Target);
        Assert.Equal(@"HKLM\" + p.RegistryKey, plan.Changes[4].Target);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Forced_plan_without_folder_still_removes_entry_and_says_why()
    {
        var p = SeededProg(null);
        var plan = Planner(() => []).Plan(p, [p]);
        Assert.Equal(["regkey"], plan.Changes.Select(c => c.Kind));
        Assert.Null(plan.Folder);
        Assert.Contains("Ordner nicht angeboten: Kein Installationsordner eingetragen.", plan.Notes);

        var dir = FooFolder();
        var running = SeededProg(dir);
        _procs.List.Add(new RunningProcess("foo.exe", dir + @"\foo.exe"));
        var trace = F("auto", dir + @"\foo.exe", Auto("foo"));
        var plan2 = Planner(() => [trace]).Plan(running, [running]);
        Assert.Equal(["autostart", "regkey"], plan2.Changes.Select(c => c.Kind));
        Assert.Null(plan2.Folder);
        Assert.Contains(plan2.Notes, n => n.Contains("Ein Programm aus diesem Ordner läuft gerade. Bitte zuerst beenden."));

        _procs.List.Clear();
        _files.Junctions.Add(dir + @"\sub");
        var plan3 = Planner(() => []).Plan(running, [running]);
        Assert.Equal(["regkey"], plan3.Changes.Select(c => c.Kind));
        Assert.Contains(plan3.Notes, n => n.Contains("Der Ordner ist nicht vollständig lesbar oder enthält Verknüpfungen."));
    }

    [Fact]
    public void Forced_plan_refuses_msi_and_protected_programs_itself()
    {
        var dir = FooFolder();
        var msi = SeededProg(dir) with { WindowsInstaller = true };
        var prot = SeededProg(dir) with { Flag = ProgramFlag.Protected };
        var trace = F("auto", dir + @"\foo.exe", Auto("foo"));

        var a = Planner(() => [trace]).Plan(msi, [msi]);
        Assert.Empty(a.Changes);
        Assert.Null(a.Folder);
        Assert.Equal([ForcedRemovalPlanner.MsiRefused], a.Notes);

        var b = Planner(() => [trace]).Plan(prot, [prot]);
        Assert.Empty(b.Changes);
        Assert.Null(b.Folder);
        Assert.Equal([ForcedRemovalPlanner.ProtectedRefused], b.Notes);
    }

    [Fact]
    public void Forced_plan_notes_a_failed_trace_scan()
    {
        var dir = FooFolder();
        var p = SeededProg(dir);
        var plan = Planner(() => throw new InvalidOperationException("WMI")).Plan(p, [p]);
        Assert.Equal(["folder", "regkey"], plan.Changes.Select(c => c.Kind));
        Assert.Equal(["Programmspuren konnten nicht gesucht werden."], plan.Notes);
    }

    [Fact]
    public void Forced_plan_for_vanished_entry_has_no_changes()
    {
        var dir = FooFolder();
        var p = Prog("{A}", location: dir);
        var plan = Planner(() => [F("auto", dir + @"\foo.exe", Auto("foo"))]).Plan(p, [p]);
        Assert.Empty(plan.Changes);
        Assert.Null(plan.Folder);
        Assert.Equal(["Der Eintrag existiert nicht mehr."], plan.Notes);
    }
}
