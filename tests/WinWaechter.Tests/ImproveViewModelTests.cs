using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ImproveViewModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-improve-" + Guid.NewGuid().ToString("N"));
    readonly ChangeGate _gate = new();
    readonly List<string> _applied = [];
    readonly RecordingDialogs _dialogs = new();
    int _collects;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // Records what it applied; fails with the given message; may carry an undo limit.
    sealed class Change(string kind, string target, List<string> log, string? fail = null, string? limit = null) : IChange, IUndoLimit
    {
        public string Kind => kind;
        public string Target => target;
        public string Description => "Ändern " + target;
        public string? UndoLimit => limit;

        public ChangeRecord Apply(SessionContext ctx)
        {
            if (fail is not null) throw new InvalidOperationException(fail);
            log.Add(target);
            return new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);
        }

        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    Change C(string target, string kind = "autostart", string? fail = null, string? limit = null) =>
        new(kind, target, _applied, fail, limit);

    static Improvement I(string id, ImprovementArea area, params IChange[] changes) =>
        new(id, area, "Titel " + id, "Warum " + id, changes);

    Improvement Sysprotect(string? fail = null) =>
        new("sec:sysprotect", ImprovementArea.Sicherheit, "Computerschutz", "Ohne Computerschutz gibt es keine Wiederherstellungspunkte.",
            [C("sys", "sysprotect", fail, UndoLimits.Partial)], RunFirst: true);

    static ImprovementScan Scan(IReadOnlyList<Improvement> items, IReadOnlyList<ActionSuggestion>? actions = null,
        IReadOnlyList<string>? notChecked = null, DateTimeOffset? at = null) =>
        new(items, actions ?? [], notChecked ?? [], at ?? DateTimeOffset.Now);

    SessionStore Store => new(_root);

    ImproveViewModel Vm(ImprovementScan scan, bool canChange = true, Func<DriveSpace?>? disk = null)
    {
        var engine = new OptimizerEngine([], Store, new ChangeFactory(), _ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt."));
        return new ImproveViewModel((_, _) =>
        {
            _collects++;
            return Task.FromResult(scan);
        }, engine, _dialogs, new ActionRunner(_dialogs, _gate), canChange, _gate, disk);
    }

    [Fact]
    public async Task Check_groups_recommendations_preselected()
    {
        var vm = Vm(Scan([I("leer", ImprovementArea.Leistung, C("p")), I("temp", ImprovementArea.Aufraeumen, C("t")),
            I("cache", ImprovementArea.Aufraeumen, C("c"))], notChecked: ["Leistung: nicht prüfbar"]));
        Assert.Null(vm.OpenCount);
        Assert.Null(vm.CheckedAt);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(["Aufräumen", "Leistung"], vm.Groups.Select(g => g.Title));
        Assert.Equal(["Titel temp", "Titel cache"], vm.Groups[0].Items.Select(i => i.Title));
        Assert.Equal("Warum temp", vm.Groups[0].Items[0].Why);
        Assert.All(vm.Groups.SelectMany(g => g.Items), i => Assert.True(i.IsSelected));
        Assert.Equal(3, vm.SelectedCount);
        Assert.Equal("Jetzt verbessern (3)", vm.ImproveText);
        Assert.Equal(3, vm.OpenCount);
        Assert.NotNull(vm.CheckedAt);
        Assert.Contains("Leistung: nicht prüfbar", vm.UncheckedText);
        Assert.False(vm.IsAllGood);

        vm.Groups[0].Items[1].IsSelected = false;
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal("Jetzt verbessern (2)", vm.ImproveText);
        Assert.Equal(3, vm.OpenCount);   // open stays what was found
    }

    [Fact]
    public async Task Improve_confirms_once_with_summary_and_one_session()
    {
        var vm = Vm(Scan([I("a", ImprovementArea.Aufraeumen, C("a1"), C("a2")), I("b", ImprovementArea.Leistung, C("b"))]));
        var raised = 0;
        vm.ChangesApplied += (_, _) => raised++;
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.ImproveCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Rechner in Ordnung bringen", confirm.Title);
        Assert.Equal("2 Verbesserungen werden angewendet. Sie stehen danach im Verlauf und lassen sich dort als Ganzes oder einzeln rückgängig machen.",
            confirm.Message);
        Assert.Equal("Jetzt verbessern", confirm.Button);
        Assert.Equal(["Ändern a1", "Ändern a2", "Ändern b"], confirm.Items);
        var session = Assert.Single(Store.List());
        Assert.Equal("Rechner in Ordnung bringen", session.Title);
        Assert.Equal(3, session.Records.Count(r => r.Status == ChangeStatus.Applied));
        Assert.Equal("3 verbessert, 0 übersprungen", vm.ResultText);
        Assert.Empty(vm.SkippedReasons);
        Assert.Equal(1, raised);
        Assert.Equal(2, _collects);   // checked again afterwards, keeping the summary

        await vm.CheckCommand.ExecuteAsync(null);   // a new check by hand clears it
        Assert.Null(vm.ResultText);
    }

    [Fact]
    public async Task Sysprotect_runs_as_own_first_session()
    {
        var vm = Vm(Scan([I("a", ImprovementArea.Aufraeumen, C("a")), Sysprotect(), I("z", ImprovementArea.Sicherheit, C("z"))]));
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.ImproveCommand.ExecuteAsync(null);

        Assert.Equal(["sys", "a", "z"], _applied);
        var sessions = Store.List();   // newest first
        Assert.Equal(["Rechner in Ordnung bringen", "Computerschutz einschalten"], sessions.Select(s => s.Title));
        Assert.Equal(["sys"], sessions[1].Records.Select(r => r.Target));
        Assert.Equal(["a", "z"], sessions[0].Records.Select(r => r.Target));
        Assert.Equal("3 verbessert, 0 übersprungen", vm.ResultText);
    }

    [Fact]
    public async Task Failure_in_first_step_or_middle_continues_and_is_reported()
    {
        var vm = Vm(Scan([Sysprotect(fail: "Dienst deaktiviert"), I("x", ImprovementArea.Aufraeumen, C("x")),
            I("y", ImprovementArea.Aufraeumen, C("y", fail: "Zugriff verweigert")), I("z", ImprovementArea.Aufraeumen, C("z"))]));
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.ImproveCommand.ExecuteAsync(null);

        Assert.Equal(["x", "z"], _applied);
        Assert.Equal("2 verbessert, 2 übersprungen", vm.ResultText);
        Assert.Equal(["Ändern sys: Änderung fehlgeschlagen: Dienst deaktiviert", "Ändern y: Änderung fehlgeschlagen: Zugriff verweigert"],
            vm.SkippedReasons);
        var sessions = Store.List();
        Assert.Equal(["Rechner in Ordnung bringen", "Computerschutz einschalten"], sessions.Select(s => s.Title));
        Assert.Equal(ChangeStatus.Failed, Assert.Single(sessions[1].Records).Status);
        Assert.Equal([ChangeStatus.Applied, ChangeStatus.Failed, ChangeStatus.Applied], sessions[0].Records.Select(r => r.Status));
        var summary = Assert.Single(_dialogs.Informs);
        Assert.Equal("2 verbessert, 2 übersprungen", summary.Message);
        Assert.Contains(vm.SkippedReasons[1], summary.Items);
    }

    [Fact]
    public async Task Actions_never_in_the_collect_button()
    {
        var runs = 0;
        var act = new SystemAction("update-signatures", "Virendefinitionen aktualisieren", "Jetzt aktualisieren", null, false,
            (_, _) =>
            {
                runs++;
                return Task.FromResult(new ActionOutcome(true, "Die Virendefinitionen sind aktuell."));
            });
        var vm = Vm(Scan([I("a", ImprovementArea.Aufraeumen, C("a"))],
            [new ActionSuggestion("defender", "Microsoft Defender", "Aktuelle Definitionen erkennen neue Schadprogramme besser.", act)]));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.OpenCount);
        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("Microsoft Defender", suggestion.Title);
        Assert.Equal("Jetzt aktualisieren", suggestion.ButtonText);
        Assert.Equal(1, vm.SelectedCount);

        await vm.ImproveCommand.ExecuteAsync(null);
        Assert.Equal(0, runs);
        Assert.Equal(["a"], _applied);
        Assert.DoesNotContain(Assert.Single(_dialogs.Confirms).Items, l => l.Contains("Virendefinitionen"));

        await vm.Suggestions.Single().RunCommand.ExecuteAsync(null);
        Assert.Equal(1, runs);
        Assert.Equal("Die Virendefinitionen sind aktuell.", vm.ResultText);
        Assert.Single(Store.List());   // the action is no session
        Assert.Equal(3, _collects);   // checked again after the action
    }

    [Fact]
    public async Task All_good_text_with_date()
    {
        var vm = Vm(Scan([], at: new DateTimeOffset(2026, 11, 13, 9, 5, 0, TimeSpan.FromHours(1))));
        Assert.False(vm.IsAllGood);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsAllGood);
        Assert.Equal("Ihr Rechner ist in Ordnung (geprüft am 13.11.2026 09:05).", vm.AllGoodText);
        Assert.Equal(0, vm.OpenCount);
        Assert.False(vm.HasItems);
        Assert.False(vm.ImproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Locked_without_admin()
    {
        var runs = 0;
        var act = new SystemAction("a", "Aktion", "Los", null, false, (_, _) =>
        {
            runs++;
            return Task.FromResult(new ActionOutcome(true, "ok"));
        });
        var vm = Vm(Scan([I("a", ImprovementArea.Aufraeumen, C("a"))], [new ActionSuggestion("a", "Aktion", "weil", act)]),
            canChange: false);

        Assert.True(vm.CheckCommand.CanExecute(null));
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowAdminNotice);
        Assert.Equal(AppEnvironment.AdminNotice, vm.AdminNotice);
        Assert.Single(vm.Groups);
        Assert.False(vm.Groups[0].Items[0].CanSelect);
        Assert.False(vm.ImproveCommand.CanExecute(null));
        Assert.False(vm.Suggestions[0].RunCommand.CanExecute(null));
        await vm.ImproveCommand.ExecuteAsync(null);
        await vm.Suggestions[0].RunCommand.ExecuteAsync(null);
        Assert.Empty(_applied);
        Assert.Equal(0, runs);
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public async Task Partial_and_store_only_marked_in_confirm()
    {
        var vm = Vm(Scan([Sysprotect(), I("app", ImprovementArea.Aufraeumen, C("news", "appx", limit: UndoLimits.StoreOnly))]));
        await vm.CheckCommand.ExecuteAsync(null);
        _dialogs.ConfirmAnswer = false;

        await vm.ImproveCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Contains("Ändern news (Nur über den Store wiederherstellbar)", confirm.Items);
        Assert.Contains("Ändern sys (Nur teilweise rückgängig)", confirm.Items);
        Assert.Empty(_applied);   // declined: nothing changed
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task Low_disk_band_and_make_space_checks()
    {
        var vm = Vm(Scan([]), disk: () => new DriveSpace(@"C:\", 8L << 30, 500L << 30));
        Assert.Null(vm.LowDiskText);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Auf Laufwerk C: sind nur noch 8,0 GB frei.", vm.LowDiskText);

        await vm.MakeSpaceCommand.ExecuteAsync(null);
        Assert.Equal(2, _collects);

        var roomy = Vm(Scan([]), disk: () => new DriveSpace(@"C:\", 200L << 30, 500L << 30));
        await roomy.CheckCommand.ExecuteAsync(null);
        Assert.Null(roomy.LowDiskText);
    }
}
