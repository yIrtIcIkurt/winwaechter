using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
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

    static Finding F(string id, bool preselected, FindingArea area = FindingArea.Autostart, Rating rating = Rating.Entbehrlich) =>
        new(id, area, id, "HKCU\\Run\\" + id, ["Grund"], rating, preselected, new NoChange(id));

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
