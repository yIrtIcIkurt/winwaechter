using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;

namespace WinWaechter.Tests;

public sealed class ViewModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-vm-" + Guid.NewGuid().ToString("N"));

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
        return new OptimizeViewModel(engine, dialogs ?? new YesDialogs(), canChange: true);
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
        var limited = new OptimizeViewModel(engine, dialogs, canChange: false);
        await limited.ScanCommand.ExecuteAsync(null);
        Assert.Equal(1, limited.SelectedCount);
        Assert.False(limited.ApplyCommand.CanExecute(null));
        Assert.True(limited.ShowAdminNotice);

        var vm = new OptimizeViewModel(engine, dialogs, canChange: true);
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
}
