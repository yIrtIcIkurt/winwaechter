using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Update;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ViewModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-vm-" + Guid.NewGuid().ToString("N"));
    readonly ChangeGate _gate = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class NoChange(string target) : IChange
    {
        public string Kind => "fake";
        public string Target => target;
        public string Description => "Fake " + target;

        public ChangeRecord Apply(SessionContext ctx) =>
            new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);

        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    sealed class YesDialogs : IDialogService
    {
        public int Confirms { get; private set; }
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            Confirms++;
            return true;
        }

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    static Finding F(string id, bool preselected, FindingArea area = FindingArea.Autostart, Rating rating = Rating.Entbehrlich,
        string? group = null, long? size = null) =>
        new(id, area, id, "HKCU\\Run\\" + id, ["Grund"], rating, preselected, new NoChange(id), SizeBytes: size, Group: group);

    OptimizeViewModel Vm(IReadOnlyList<Finding> findings, Func<string, RestorePointResult>? rp = null, IDialogService? dialogs = null)
    {
        var engine = new OptimizerEngine([() => findings], new SessionStore(_root), new ChangeFactory(),
            rp ?? (_ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt.")));
        return new OptimizeViewModel(engine, dialogs ?? new YesDialogs(), canChange: true, _gate);
    }

    [Fact]
    public async Task Apply_count_reflects_selection()
    {
        var vm = Vm([F("a", true), F("b", true), F("c", false, FindingArea.Dienst, Rating.Info)]);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal("Ausgewählte anwenden (2)", vm.ApplyText);

        vm.AllItems.Single(i => i.Title == "c").IsSelected = true;
        Assert.Equal(3, vm.SelectedCount);
        Assert.Equal("Ausgewählte anwenden (3)", vm.ApplyText);

        foreach (var i in vm.AllItems) i.IsSelected = false;
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_disabled_while_running()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var vm = Vm([F("a", true)], _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return new RestorePointResult(false, "kein Punkt");
        });
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        var run = vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(vm.IsBusy);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.ScanCommand.CanExecute(null));

        gate.Set();
        await run;
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.AllItems);   // the applied item leaves the preview
    }

    [Fact]
    public async Task Unknown_items_not_selected_by_default()
    {
        var unknown = F("unbekannt", false, FindingArea.Aufgabe, Rating.Info);
        var known = F("bekannt", true);
        var vm = Vm([unknown, known]);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.AllItems.Single(i => i.Title == "unbekannt").IsSelected);
        Assert.True(vm.AllItems.Single(i => i.Title == "bekannt").IsSelected);
        Assert.Equal(["Autostart", "Aufgaben"], vm.Groups.Select(g => g.Title));
    }

    [Fact]
    public async Task Apply_asks_for_confirmation_and_is_blocked_without_admin_rights()
    {
        var dialogs = new YesDialogs();
        var engine = new OptimizerEngine([() => [F("a", true)]], new SessionStore(_root), new ChangeFactory(),
            _ => new RestorePointResult(true, "ok"));
        var limited = new OptimizeViewModel(engine, dialogs, canChange: false, new ChangeGate());
        await limited.ScanCommand.ExecuteAsync(null);
        Assert.Equal(1, limited.SelectedCount);
        Assert.False(limited.ApplyCommand.CanExecute(null));
        Assert.True(limited.ShowAdminNotice);

        var vm = new OptimizeViewModel(engine, dialogs, canChange: true, new ChangeGate());
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.Confirms);
    }

    [Fact]
    public void Purged_record_is_not_undoable()
    {
        var store = new SessionStore(_root);
        var j = store.Create("T");
        var r = new ChangeRecord("1", "temp", "x", "Temp-Dateien", new(), new() { ["purged"] = "true" }, ChangeStatus.Applied, DateTimeOffset.Now);
        j.Upsert(r);
        var s = new SessionViewModel(j);
        Assert.False(s.Records[0].CanUndo);
        Assert.Equal("Ablage geleert", s.Records[0].Hint);
        Assert.Equal("Angewendet", s.Records[0].StatusText);
        Assert.False(s.CanUndoAll);
    }

    // ---- update install order ----

    sealed class RecordingUpdates(List<string> log, string setupPath) : IUpdateService
    {
        public static readonly UpdateInfo Info = new(new SemVer(0, 2, 0), "Neu.", new Uri("https://github.com/a"),
            new Uri("https://github.com/b"), new Uri("https://github.com/c"), "WinWaechter-Setup-0.2.0.exe");

        public FileStream? Held { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) =>
            Task.FromResult(new UpdateCheckResult(Info, "Version 0.2.0 ist verfügbar."));

        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            Held = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(new VerifiedUpdate(setupPath, info.Version, Held));
        }

        public void Launch(VerifiedUpdate update) => log.Add(Held!.CanRead ? "launch, file held" : "launch, file released");
    }

    [Fact]
    public async Task Install_launches_while_verified_file_is_held_then_disposes_then_shuts_down()
    {
        Directory.CreateDirectory(_root);
        var setup = Path.Combine(_root, "WinWaechter-Setup-0.2.0.exe");
        File.WriteAllText(setup, "x");
        var log = new List<string>();
        var svc = new RecordingUpdates(log, setup);
        var vm = new UpdateViewModel(svc, new YesDialogs(), canInstall: true,
            () => log.Add(svc.Held!.CanRead ? "shutdown, file held" : "shutdown, file released"), _gate);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsAvailable);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.InstallCommand.Execute(null);

        Assert.Equal(["launch, file held", "shutdown, file released"], log);
        Assert.False(vm.IsVerified);
    }

    // ---- admin gating and the shared change gate ----

    static Finding Heur(string id) =>
        new(id, FindingArea.Prozess, id + ".exe", Path.Combine(Path.GetTempPath(), id + ".exe"), ["Grund"], Rating.Verdaechtig, true, new NoChange(id));

    ProtectViewModel Protect(bool canChange, OptimizerEngine engine) =>
        new(new FakeDefender { PuaMode = 0 }, (_, _) => Task.FromResult<IReadOnlyList<Finding>>([Heur("m")]),
            engine, _ => 0, new YesDialogs(), canChange, _gate);

    [Theory]
    [InlineData(2, "2 Prozesse beendet.")]
    [InlineData(0, "Läuft nicht mehr.")]
    public async Task Kill_hides_the_button_and_notes_it_on_the_card(int killed, string notePrefix)
    {
        var p = new ProtectViewModel(new FakeDefender(), (_, _) => Task.FromResult<IReadOnlyList<Finding>>([Heur("m")]),
            Engine(), _ => killed, new YesDialogs(), true, _gate);
        await p.HeuristicScanCommand.ExecuteAsync(null);
        var item = p.HeuristicItems.Single();
        Assert.True(p.KillProcessCommand.CanExecute(item));

        await p.KillProcessCommand.ExecuteAsync(item);

        Assert.False(item.CanKill);
        Assert.False(p.KillProcessCommand.CanExecute(item));
        Assert.StartsWith(notePrefix, item.ProcessNote);
    }

    [Fact]
    public async Task After_a_new_change_the_history_selects_the_new_session()
    {
        var store = new SessionStore(_root);
        store.Create("alt");
        var h = new HistoryViewModel(store, Engine(), new AblageJanitor(store), new YesDialogs(), true, _gate);
        await h.RefreshCommand.ExecuteAsync(null);
        var old = h.SelectedSession!;

        Thread.Sleep(1100);   // session folders are named by second
        store.Create("neu");
        await h.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(old.Journal.Context.SessionDir, h.SelectedSession!.Journal.Context.SessionDir);   // plain refresh keeps the choice

        await h.ShowNewestAsync();
        Assert.Equal("neu", h.SelectedSession!.Journal.Title);
    }

    OptimizerEngine Engine(Func<string, RestorePointResult>? rp = null) =>
        new([() => [F("a", true)]], new SessionStore(_root), new ChangeFactory(), rp ?? (_ => new RestorePointResult(true, "ok")));

    async Task<HistoryViewModel> History(bool canChange, OptimizerEngine engine)
    {
        var store = new SessionStore(_root);
        if (store.List().Count == 0)
            store.Create("T").Upsert(new ChangeRecord("1", "fake", "x", "Fake x", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now));
        var h = new HistoryViewModel(store, engine, new AblageJanitor(store), new YesDialogs(), canChange, _gate);
        await h.RefreshCommand.ExecuteAsync(null);
        return h;
    }

    [Fact]
    public async Task Protect_and_history_changes_are_disabled_without_admin_rights()
    {
        var engine = Engine();
        foreach (var admin in new[] { false, true })
        {
            var p = Protect(admin, engine);
            await p.RefreshCommand.ExecuteAsync(null);
            await p.HeuristicScanCommand.ExecuteAsync(null);
            var item = p.HeuristicItems.Single();
            Assert.True(p.ShowPuaSuggestion);
            Assert.Equal(admin, p.EnablePuaCommand.CanExecute(null));
            Assert.Equal(admin, p.QuickScanCommand.CanExecute(null));
            Assert.Equal(admin, p.DisableCommand.CanExecute(item));
            Assert.Equal(admin, p.KillProcessCommand.CanExecute(item));
            Assert.Equal(admin, p.CheckWithDefenderCommand.CanExecute(item));
            Assert.Equal(!admin, p.ShowAdminNotice);

            var h = await History(admin, engine);
            var session = h.Sessions.Single();
            Assert.Equal(admin, h.UndoOneCommand.CanExecute(session.Records.Single()));
            Assert.Equal(admin, h.UndoAllCommand.CanExecute(session));
        }
    }

    [Fact]
    public async Task Change_gate_blocks_other_change_commands_while_one_runs()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var engine = Engine(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new RestorePointResult(true, "ok");
        });
        var optimize = new OptimizeViewModel(engine, new YesDialogs(), canChange: true, _gate);
        var protect = Protect(true, engine);
        await protect.RefreshCommand.ExecuteAsync(null);
        var history = await History(true, engine);
        var rec = history.Sessions.Single().Records.Single();
        await optimize.ScanCommand.ExecuteAsync(null);
        Assert.True(protect.EnablePuaCommand.CanExecute(null));
        Assert.True(history.UndoOneCommand.CanExecute(rec));

        var run = optimize.ApplyCommand.ExecuteAsync(null);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(_gate.IsBusy);
        Assert.False(protect.EnablePuaCommand.CanExecute(null));
        Assert.False(history.UndoOneCommand.CanExecute(rec));
        Assert.False(history.PurgeAblageCommand.CanExecute(null));
        Assert.Equal(ChangeGate.BusyHint, protect.GateHint);
        Assert.Equal(ChangeGate.BusyHint, history.GateHint);
        Assert.Null(optimize.GateHint);   // the page running the change shows its own progress

        release.Set();
        await run;
        Assert.False(_gate.IsBusy);
        Assert.True(protect.EnablePuaCommand.CanExecute(null));
        Assert.Null(protect.GateHint);
    }

    [Fact]
    public async Task Change_gate_refuses_a_second_run()
    {
        var g = new ChangeGate();
        var release = new TaskCompletionSource();
        var first = g.RunAsync(this, () => release.Task);
        var ran = false;
        Assert.False(await g.RunAsync(new object(), () => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
        release.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task Apply_summary_after_cancel_names_applied_and_not_executed()
    {
        var dialogs = new RecordingDialogs();
        OptimizeViewModel? vm = null;
        // the first change cancels the run while it is applied; the engine then stops before the next one
        var first = new CallbackChange("a", () => vm!.ApplyCancelCommand.Execute(null));
        Finding[] findings =
        [
            new("a", FindingArea.Autostart, "a", "loc", ["Grund"], Rating.Entbehrlich, true, first),
            F("b", true), F("c", true),
        ];
        var engine = new OptimizerEngine([() => findings], new SessionStore(_root), new ChangeFactory(),
            _ => new RestorePointResult(true, "ok"));
        vm = new OptimizeViewModel(engine, dialogs, canChange: true, _gate);
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Abgebrochen: 1 angewendet, 2 nicht ausgeführt.", vm.LastSummary);
        Assert.StartsWith("Abgebrochen: 1 angewendet, 2 nicht ausgeführt.", dialogs.LastMessage);
        Assert.Equal(2, vm.AllItems.Count);
    }

    // ---- final review fixes ----

    [Fact]
    public void Protected_vendor_finding_offers_only_the_defender_check()
    {
        var f = new Finding("heur|x", FindingArea.Prozess, "DellUpdate.exe", @"C:\x\DellUpdate.exe", ["Grund"],
            Rating.Auffaellig, false, null, IsProtectedVendor: true);
        var item = new HeuristicItemViewModel(f);
        Assert.True(item.IsProcess);
        Assert.False(item.CanKill);
        Assert.False(item.CanDisable);

        var p = Protect(true, Engine());
        Assert.False(p.KillProcessCommand.CanExecute(item));
        Assert.False(p.DisableCommand.CanExecute(item));
        Assert.True(p.CheckWithDefenderCommand.CanExecute(item));
        Assert.True(p.KillProcessCommand.CanExecute(new HeuristicItemViewModel(Heur("m"))));
    }

    [Fact]
    public async Task Scans_update_signatures_first_and_continue_when_that_fails()
    {
        var d = new FakeDefender();
        var p = new ProtectViewModel(d, (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]), Engine(), _ => 0,
            new YesDialogs(), true, _gate);

        await p.QuickScanCommand.ExecuteAsync(null);
        Assert.Equal(["update", "scan:Quick"], d.Calls);
        Assert.DoesNotContain("Hinweis", p.DefenderResult);

        d.Calls.Clear();
        d.UpdateError = new IOException("kein Netz");
        await p.FullScanCommand.ExecuteAsync(null);
        Assert.Equal(["update", "scan:Full"], d.Calls);
        Assert.StartsWith("Vollscan abgeschlossen.", p.DefenderResult);
        Assert.Contains("Signaturen ließen sich nicht aktualisieren", p.DefenderResult);
    }

    [Fact]
    public async Task Clean_shows_where_quarantined_files_can_be_restored()
    {
        var d = new FakeDefender();
        d.Threats.Add(new DefenderThreat(1, "Trojan:X", 5, [@"file:C:\x.exe"], true, "Aktiv"));
        var p = new ProtectViewModel(d, (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]), Engine(), _ => 0,
            new YesDialogs(), true, _gate);
        await p.RefreshCommand.ExecuteAsync(null);
        Assert.Null(p.CleanHint);

        await p.CleanCommand.ExecuteAsync(null);

        Assert.Contains("remove", d.Calls);
        Assert.Equal(ProtectViewModel.RestoreHint, p.CleanHint);
        Assert.Contains("Schutzverlauf", p.CleanHint);
    }

    [Fact]
    public void Temp_record_that_moved_nothing_shows_hint_and_no_undo()
    {
        var j = new SessionStore(_root).Create("T");
        j.Upsert(new ChangeRecord("1", "temp", "x", "Temp-Dateien", new(), new() { ["moved"] = "0" }, ChangeStatus.Applied, DateTimeOffset.Now));
        var s = new SessionViewModel(j);
        Assert.False(s.Records[0].CanUndo);
        Assert.Equal("Nichts verschoben", s.Records[0].Hint);
        Assert.False(s.CanUndoAll);
    }

    [Fact]
    public async Task Purge_needs_admin_rights()
    {
        var store = new SessionStore(_root);
        AblageFolder(store.Create("T"), "a", ChangeStatus.Applied, "12345");
        foreach (var admin in new[] { false, true })
        {
            var h = new HistoryViewModel(store, Engine(), new AblageJanitor(store), new YesDialogs(), admin, _gate);
            await h.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(5, h.AblageBytes);
            Assert.Equal(admin, h.PurgeAblageCommand.CanExecute(null));
        }
    }

    // One Ablage subfolder with a file, named by a temp record with the given status.
    static void AblageFolder(SessionJournal j, string id, ChangeStatus status, string content, string kind = "temp")
    {
        var dir = Path.Combine(j.Context.AblageDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.tmp"), content);
        j.Upsert(new ChangeRecord(id, kind, "t", "Temp", new(), new() { ["ablage"] = dir }, status, DateTimeOffset.Now));
    }

    sealed class ConfirmRecorder : IDialogService
    {
        public string? LastConfirm { get; private set; }
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            LastConfirm = message;
            return true;
        }

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    [Fact]
    public async Task Ablage_size_and_purge_dialog_count_only_what_is_really_deleted()
    {
        var store = new SessionStore(_root);
        var j = store.Create("T");
        AblageFolder(j, "done", ChangeStatus.Applied, "12345");
        AblageFolder(j, "half", ChangeStatus.Failed, "1234567890");
        AblageFolder(j, "crash", ChangeStatus.Pending, "1234567890");
        var dialogs = new ConfirmRecorder();
        var h = new HistoryViewModel(store, Engine(), new AblageJanitor(store), dialogs, true, _gate);

        await h.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(5, h.AblageBytes);
        Assert.Equal("2 Ordner aus unvollständigen Vorgängen bleiben erhalten.", h.KeptText);

        await h.PurgeAblageCommand.ExecuteAsync(null);
        Assert.Contains("2 Ordner aus unvollständigen Vorgängen bleiben erhalten.", dialogs.LastConfirm);
        Assert.Equal(0, h.AblageBytes);
        Assert.True(Directory.Exists(Path.Combine(j.Context.AblageDir, "half")));
        Assert.False(Directory.Exists(Path.Combine(j.Context.AblageDir, "done")));
    }

    [Fact]
    public void Old_update_downloads_are_removed_fresh_ones_kept()
    {
        var updates = Directory.CreateDirectory(Path.Combine(_root, "Updates")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(updates, "0.2.0-aaaa")).FullName;
        File.WriteAllText(Path.Combine(old, "WinWaechter-Setup-0.2.0.exe"), "x");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        var fresh = Directory.CreateDirectory(Path.Combine(updates, "0.3.0-bbbb")).FullName;

        Assert.Equal(1, UpdateCleanup.PurgeOld(updates, UpdateCleanup.DefaultMaxAge));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(0, UpdateCleanup.PurgeOld(Path.Combine(_root, "missing"), UpdateCleanup.DefaultMaxAge));
    }

    // ---- 0.2 groups, "Wird frei", purge dialog ----

    [Fact]
    public async Task Groups_follow_fixed_order_and_sum_sizes()
    {
        var vm = Vm([
            F("leftover", false, FindingArea.Speicher, Rating.Info, FindingGroups.OrphanFolders, 10),
            F("edge", true, FindingArea.Speicher, group: FindingGroups.BrowserCache, size: 3),
            F("chrome", true, FindingArea.Speicher, group: FindingGroups.BrowserCache, size: 4),
            F("temp", true, FindingArea.Speicher, size: 1),
            F("auto", true),
        ]);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(["Autostart", "Speicher", "Browser-Cache", "Verwaiste Ordner"], vm.Groups.Select(g => g.Title));
        Assert.Equal(Labels.Size(7), vm.Groups.Single(g => g.Title == "Browser-Cache").SizeText);
        Assert.Equal(LeftoverFolderScanner.GroupHint, vm.Groups.Single(g => g.Title == "Verwaiste Ordner").Hint);
        Assert.Null(vm.Groups.Single(g => g.Title == "Browser-Cache").Hint);
        Assert.Null(vm.Groups.Single(g => g.Title == "Autostart").SizeText);
    }

    [Fact]
    public void Group_size_updates_when_items_are_removed()
    {
        var items = new[] { F("a", true, size: 3), F("b", true, size: 4) }.Select(f => new FindingItemViewModel(f)).ToList();
        var g = new FindingGroupViewModel("Browser-Cache", items);
        var changed = new List<string?>();
        g.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        g.Items.Remove(items[0]);

        Assert.Equal(Labels.Size(4), g.SizeText);
        Assert.Contains(nameof(FindingGroupViewModel.SizeText), changed);
    }

    [Fact]
    public async Task Orphan_entries_group_collects_entries_from_all_areas()
    {
        var vm = Vm([
            F("run", true, FindingArea.Autostart, group: FindingGroups.OrphanEntries),
            F("svc", true, FindingArea.Dienst, group: FindingGroups.OrphanEntries),
            F("task", true, FindingArea.Aufgabe, group: FindingGroups.OrphanEntries),
        ]);
        await vm.ScanCommand.ExecuteAsync(null);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Verwaiste Einträge", group.Title);
        Assert.Equal(3, group.Items.Count);
    }

    [Fact]
    public async Task SelectedBytes_counts_selected_items_with_change()
    {
        var noChange = new Finding("nc", FindingArea.Speicher, "nc", @"C:\x", ["Grund"], Rating.Info, true, null, SizeBytes: 9);
        var vm = Vm([F("five", true, FindingArea.Speicher, size: 5), F("seven", false, FindingArea.Speicher, size: 7), noChange]);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.SelectedBytes);

        raised.Clear();
        vm.AllItems.Single(i => i.Title == "seven").IsSelected = true;
        Assert.Equal(12, vm.SelectedBytes);
        Assert.Contains(nameof(OptimizeViewModel.SelectedBytes), raised);
    }

    [Fact]
    public async Task Overview_free_text_dash_before_scan_then_size()
    {
        var optimize = Vm([F("five", true, FindingArea.Speicher, size: 5), F("seven", false, FindingArea.Speicher, size: 7)]);
        var update = new UpdateViewModel(new RecordingUpdates([], Path.Combine(_root, "x.exe")), new YesDialogs(), false, () => { }, _gate);
        var overview = new OverviewViewModel(optimize, Protect(true, Engine()), update, () => { });
        var raised = new List<string?>();
        overview.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("–", overview.FreeText);

        await optimize.ScanCommand.ExecuteAsync(null);
        Assert.Equal(Labels.Size(5), overview.FreeText);

        raised.Clear();
        optimize.AllItems.Single(i => i.Title == "seven").IsSelected = true;
        Assert.Equal(Labels.Size(12), overview.FreeText);
        Assert.Contains(nameof(OverviewViewModel.FreeText), raised);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "Darin enthalten: 1 Programmordner aus „Verwaiste Ordner“.")]
    [InlineData(3, "Darin enthalten: 3 Programmordner aus „Verwaiste Ordner“.")]
    public void ProgramFoldersText_wording(int n, string? expected) =>
        Assert.Equal(expected, HistoryViewModel.ProgramFoldersText(n));

    [Fact]
    public async Task Purge_dialog_names_program_folders()
    {
        var store = new SessionStore(_root);
        AblageFolder(store.Create("T"), "prog", ChangeStatus.Applied, "12345", kind: "folder");
        var dialogs = new ConfirmRecorder();
        var h = new HistoryViewModel(store, Engine(), new AblageJanitor(store), dialogs, true, _gate);
        await h.RefreshCommand.ExecuteAsync(null);

        await h.PurgeAblageCommand.ExecuteAsync(null);

        Assert.Contains("Darin enthalten: 1 Programmordner", dialogs.LastConfirm);
    }

    sealed class CallbackChange(string target, Action onApply) : IChange
    {
        public string Kind => "fake";
        public string Target => target;
        public string Description => "Fake " + target;

        public ChangeRecord Apply(SessionContext ctx)
        {
            onApply();
            return new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);
        }

        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    sealed class RecordingDialogs : IDialogService
    {
        public string? LastMessage { get; private set; }

        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) => true;

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) => LastMessage = message;
    }
}
