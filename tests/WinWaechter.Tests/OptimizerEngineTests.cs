using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;

namespace WinWaechter.Tests;

public sealed class OptimizerEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-eng-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _undoLog = new();
    readonly Dictionary<string, FakeChange> _changes = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class FakeChange : IChange
    {
        readonly List<string> _log;
        public FakeChange(string target, List<string> log, string kind) { Target = target; _log = log; Kind = kind; }
        public string Kind { get; }
        public string Target { get; }
        public string Description => "Fake " + Target;
        public bool ThrowOnApply { get; init; }
        public bool ThrowOnUndo { get; init; }
        public DriftState Drift { get; init; } = DriftState.Unchanged;
        public Action? OnApply { get; set; }

        public ChangeRecord Apply(SessionContext ctx)
        {
            OnApply?.Invoke();
            if (ThrowOnApply) throw new InvalidOperationException("boom");
            return new ChangeRecord("other-id", Kind, Target, Description, new(), new() { ["x"] = "1" },
                ChangeStatus.Applied, DateTimeOffset.Now);
        }

        public void Undo(ChangeRecord record, SessionContext ctx)
        {
            if (ThrowOnUndo) throw new IOException("undo kaputt");
            _log.Add(Target);
        }

        public DriftState CheckDrift(ChangeRecord record) => Drift;
    }

    FakeChange Fake(string target, bool throwApply = false, bool throwUndo = false,
        DriftState drift = DriftState.Unchanged, string kind = "fake")
    {
        var c = new FakeChange(target, _undoLog, kind) { ThrowOnApply = throwApply, ThrowOnUndo = throwUndo, Drift = drift };
        _changes[target] = c;
        return c;
    }

    static Finding F(IChange? c, string id) =>
        new(id, FindingArea.Autostart, id, "loc", new[] { "r" }, Rating.Entbehrlich, true, c);

    OptimizerEngine Engine(Func<string, RestorePointResult>? rp = null)
    {
        var factory = new ChangeFactory();
        foreach (var kind in new[] { "fake", "task", "temp" })
            factory.Register(kind, r => _changes[r.Target]);
        return new OptimizerEngine(Array.Empty<Func<IReadOnlyList<Finding>>>(), new SessionStore(_root), factory,
            rp ?? (_ => new RestorePointResult(true, "ok")));
    }

    Task<ApplyResult> Apply(OptimizerEngine e, params FakeChange[] cs) =>
        e.ApplyAsync("T", cs.Select(c => F(c, c.Target)).ToList(), null, CancellationToken.None);

    [Fact]
    public async Task Failure_mid_apply_stops_and_records()
    {
        var cs = Enumerable.Range(1, 5).Select(i => Fake("c" + i, throwApply: i == 3)).ToArray();
        var res = await Apply(Engine(), cs);
        Assert.Equal(2, res.Applied);
        Assert.Equal(1, res.Failed);
        var recs = res.Session.Records;
        Assert.Equal(new[] { ChangeStatus.Applied, ChangeStatus.Applied, ChangeStatus.Failed }, recs.Select(r => r.Status));
        Assert.Equal(new[] { "c1", "c2", "c3" }, recs.Select(r => r.Target));
        Assert.Contains("boom", recs[2].Error);
        // the pre-allocated id wins over the one the change returned
        Assert.DoesNotContain(recs, r => r.Id == "other-id");
    }

    [Fact]
    public async Task Pending_record_is_written_before_the_change_runs()
    {
        var c = Fake("c1");
        var store = new SessionStore(_root);
        ChangeStatus? seen = null;
        c.OnApply = () => seen = store.List().Single().Records.Single().Status;
        var e = new OptimizerEngine(Array.Empty<Func<IReadOnlyList<Finding>>>(), store, new ChangeFactory(),
            _ => new RestorePointResult(true, "ok"));
        var res = await e.ApplyAsync("T", new[] { F(c, "c1") }, null, CancellationToken.None);
        Assert.Equal(ChangeStatus.Pending, seen);
        Assert.Equal(ChangeStatus.Applied, res.Session.Records.Single().Status);
    }

    [Fact]
    public async Task Findings_without_change_are_ignored_and_restore_point_runs_first()
    {
        var order = new List<string>();
        var c = Fake("c1");
        c.OnApply = () => order.Add("apply");
        var e = Engine(t => { order.Add("rp:" + t); return new RestorePointResult(false, "aus"); });
        var res = await e.ApplyAsync("Titel", new[] { F(null, "x"), F(c, "c1") }, null, CancellationToken.None);
        Assert.Equal(new[] { "rp:Titel", "apply" }, order);
        Assert.Equal(1, res.Applied);
        Assert.False(res.RestorePoint.Created);
        Assert.Equal("Titel", res.Session.Title);
    }

    [Fact]
    public async Task UndoSession_reverses_order()
    {
        var res = await Apply(Engine(), Fake("c1"), Fake("c2"));
        var u = Engine().UndoSession(res.Session);
        Assert.Equal(new[] { "c2", "c1" }, _undoLog);
        Assert.Equal(2, u.Undone);
        Assert.All(res.Session.Records, r => Assert.Equal(ChangeStatus.Undone, r.Status));
    }

    [Fact]
    public async Task Drift_item_is_skipped_others_undone()
    {
        var res = await Apply(Engine(), Fake("c1"), Fake("c2", drift: DriftState.Changed), Fake("c3"));
        var u = Engine().UndoSession(res.Session);
        Assert.Equal(2, u.Undone);
        Assert.Equal(1, u.SkippedDrift);
        Assert.Equal("Fake c2: seit der Änderung verändert – übersprungen", Assert.Single(u.Messages));
        Assert.Equal(ChangeStatus.SkippedDrift, res.Session.Records.Single(r => r.Target == "c2").Status);
        Assert.Equal(new[] { "c3", "c1" }, _undoLog);
    }

    [Fact]
    public async Task Gone_is_skipped_except_for_tasks()
    {
        var res = await Apply(Engine(), Fake("c1", drift: DriftState.Gone), Fake("t1", drift: DriftState.Gone, kind: "task"));
        var u = Engine().UndoSession(res.Session);
        Assert.Equal(1, u.Undone);
        Assert.Equal(1, u.SkippedDrift);
        Assert.Equal("Fake c1: nicht mehr vorhanden – übersprungen", Assert.Single(u.Messages));
        Assert.Equal(new[] { "t1" }, _undoLog);
    }

    [Fact]
    public async Task Undo_exception_keeps_applied_counts_failed_and_continues()
    {
        var res = await Apply(Engine(), Fake("c1"), Fake("c2", throwUndo: true));
        var u = Engine().UndoSession(res.Session);
        Assert.Equal(1, u.Undone);
        Assert.Equal(1, u.Failed);
        Assert.Contains("Fake c2", Assert.Single(u.Messages));
        var r = res.Session.Records.Single(x => x.Target == "c2");
        Assert.Equal(ChangeStatus.Applied, r.Status);
        Assert.Contains("undo kaputt", r.Error);
        Assert.Equal(new[] { "c1" }, _undoLog);
    }

    [Fact]
    public async Task Purged_ablage_record_is_skipped_with_message()
    {
        var res = await Apply(Engine(), Fake("f1", kind: "temp"));
        var rec = res.Session.Records.Single();
        res.Session.Upsert(rec with { After = new Dictionary<string, string?>(rec.After) { ["purged"] = "true" } });
        var u = Engine().UndoSession(res.Session);
        Assert.Equal(1, u.SkippedDrift);
        Assert.Equal("Fake f1: Ablage wurde bereits geleert – nicht wiederherstellbar", Assert.Single(u.Messages));
        Assert.Empty(_undoLog);
    }

    [Fact]
    public async Task UndoOne_only_touches_that_record()
    {
        var res = await Apply(Engine(), Fake("c1"), Fake("c2"));
        var id = res.Session.Records.Single(r => r.Target == "c1").Id;
        var u = Engine().UndoOne(res.Session, id);
        Assert.Equal(1, u.Undone);
        Assert.Equal(new[] { "c1" }, _undoLog);
        Assert.Equal(ChangeStatus.Applied, res.Session.Records.Single(r => r.Target == "c2").Status);
    }

    [Fact]
    public async Task Cancellation_before_item_stops_without_partial_record()
    {
        using var cts = new CancellationTokenSource();
        var c1 = Fake("c1");
        c1.OnApply = cts.Cancel;
        var c2 = Fake("c2");
        var res = await Engine().ApplyAsync("T", new[] { F(c1, "c1"), F(c2, "c2") }, null, cts.Token);
        Assert.Equal(1, res.Applied);
        var rec = Assert.Single(res.Session.Records);
        Assert.Equal("c1", rec.Target);
        Assert.Equal(ChangeStatus.Applied, rec.Status);
    }

    [Fact]
    public async Task Scan_skips_throwing_scanner_and_merges_others()
    {
        var f1 = F(null, "a");
        var f2 = F(null, "b");
        var e = new OptimizerEngine(new Func<IReadOnlyList<Finding>>[]
        {
            () => new[] { f1 },
            () => throw new InvalidOperationException("scanner kaputt"),
            () => new[] { f2 },
        }, new SessionStore(_root), new ChangeFactory(), _ => new RestorePointResult(true, ""));
        var found = await e.ScanAsync(null, CancellationToken.None);
        Assert.Equal(new[] { "a", "b" }, found.Select(f => f.Id));
    }

    [Fact]
    public void ChangeFactory_unknown_kind_throws_clear_exception()
    {
        var f = new ChangeFactory();
        var r = new ChangeRecord("1", "nix", "t", "d", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);
        var ex = Assert.Throws<InvalidOperationException>(() => f.Rehydrate(r));
        Assert.Contains("nix", ex.Message);
    }

    [Fact]
    public async Task Cancelled_before_start_creates_no_session_or_restore_point()
    {
        var called = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var e = Engine(_ => { called = true; return new RestorePointResult(true, ""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            e.ApplyAsync("T", new[] { F(Fake("c1"), "c1") }, null, cts.Token));
        Assert.False(called);
        Assert.Empty(new SessionStore(_root).List());
    }

    [Fact]
    public void RestorePoint_scope_path_is_valid_wmi_path()
    {
        Assert.Equal(@"\\.\root\default", RestorePointService.ScopePath);
    }
}
