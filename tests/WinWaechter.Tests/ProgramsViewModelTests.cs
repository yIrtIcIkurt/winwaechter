using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ProgramsViewModelTests : IDisposable
{
    const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Exe = @"C:\Program Files\Foo\unins000.exe";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-progvm-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly ChangeGate _gate = new();
    readonly Dialogs _dialogs = new();
    readonly RecordingShell _shell = new();
    readonly FakeProgramActions _actions = new();
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;

    public ProgramsViewModelTests()
    {
        _store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(_reg, new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), RuleSet.Default);
        _engine = new OptimizerEngine([], _store, factory, _ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Dialogs : IDialogService
    {
        public Func<string, bool> ConfirmResult { get; set; } = _ => true;
        public bool AskResult { get; set; } = true;
        public List<(string Title, string Message, string ConfirmText, IReadOnlyList<string> Items, bool Final)> Confirms { get; } = [];
        public List<(string Title, string Message, string Yes, string No)> Asks { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Items)> Informs { get; } = [];

        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            Confirms.Add((title, message, confirmText, items ?? [], final));
            return ConfirmResult(title);
        }

        public bool Ask(string title, string message, string yesText, string noText)
        {
            Asks.Add((title, message, yesText, noText));
            return AskResult;
        }

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) => Informs.Add((title, message, items ?? []));
    }

    sealed class RecordingShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    // applies without touching anything and journals an Applied record
    sealed class OkChange(string kind, string target, string description) : IChange
    {
        public string Kind => kind;
        public string Target => target;
        public string Description => description;
        public ChangeRecord Apply(SessionContext ctx) =>
            new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new() { ["x"] = "1" }, ChangeStatus.Applied, DateTimeOffset.Now);
        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    static InstalledProgram P(string name, string? publisher = "Foo GmbH", long? size = null, DateTime? date = null,
        ProgramFlag flag = ProgramFlag.None, bool msi = false) =>
        new(Hive.LocalMachine, Root, name + "_is1", name, publisher, "1.0", date, size, "\"" + Exe + "\"",
            @"C:\Program Files\" + name, null, msi, flag);

    static UninstallPlan LaunchPlan => new(UninstallRoute.Launch, new UninstallCommand(Exe, "/S"), "", false);

    ProgramsViewModel Vm(bool canChange = true) => new(_actions, _engine, _dialogs, _shell, canChange, _gate);

    async Task<ProgramsViewModel> Loaded(params InstalledProgram[] programs)
    {
        _actions.Programs = programs.ToList();
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    static ProgramItemViewModel Row(ProgramsViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    [Fact]
    public async Task User_route_starts_directly_and_says_so()
    {
        var plan = new UninstallPlan(UninstallRoute.LaunchAsUser, new UninstallCommand(Exe, ""), UninstallPlanner.UserWritableReason, false);
        _actions.PlanFor = _ => plan;
        var vm = await Loaded(P("Vavoo"));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Vavoo"));

        Assert.Equal(plan, Assert.Single(_actions.Runs));
        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Deinstallieren", confirm.Title);
        Assert.Contains(ProgramsViewModel.AsUserText, confirm.Items);
        Assert.Empty(_shell.Links);
    }

    [Fact]
    public async Task User_route_falls_back_to_settings_when_it_cannot_start()
    {
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.LaunchAsUser, new UninstallCommand(Exe, ""), UninstallPlanner.UserWritableReason, false);
        _actions.RunThrows = new UserLaunchException(UninstallPlanner.NeedsAdminReason, 740);
        var vm = await Loaded(P("Vavoo"));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Vavoo"));

        Assert.Equal(2, _dialogs.Confirms.Count);
        var settings = _dialogs.Confirms[1];
        Assert.Equal("Über Windows-Einstellungen deinstallieren", settings.Title);
        Assert.StartsWith(UninstallPlanner.NeedsAdminReason, settings.Message);
        Assert.Equal([SystemLinks.AppsFeatures], _shell.Links);
        Assert.Equal("Vavoo", vm.Pending!.DisplayName);
        Assert.Equal("", vm.ProgressText);
        Assert.Empty(_dialogs.Informs);
    }

    [Fact]
    public async Task User_route_fallback_declined_opens_nothing()
    {
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.LaunchAsUser, new UninstallCommand(Exe, ""), UninstallPlanner.UserWritableReason, false);
        _actions.RunThrows = new UserLaunchException(UninstallPlanner.NoUserLaunchReason, 5);
        _dialogs.ConfirmResult = t => t == "Deinstallieren";
        var vm = await Loaded(P("Vavoo"));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Vavoo"));

        Assert.Equal(2, _dialogs.Confirms.Count);
        Assert.Empty(_shell.Links);
        Assert.Null(vm.Pending);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Other_launch_errors_still_show_the_error()
    {
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.LaunchAsUser, new UninstallCommand(Exe, ""), UninstallPlanner.UserWritableReason, false);
        _actions.RunThrows = new InvalidOperationException("kaputt");
        var vm = await Loaded(P("Vavoo"));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Vavoo"));

        Assert.Equal(("Deinstallation fehlgeschlagen", "kaputt"), (_dialogs.Informs.Single().Title, _dialogs.Informs.Single().Message));
        Assert.Empty(_shell.Links);
    }

    [Fact]
    public void Settings_reason_fits_the_entry()
    {
        var needsAdmin = new UserLaunchException(UninstallPlanner.NeedsAdminReason, 740);
        var machine = P("Foo");
        var user = machine with { Hive = Hive.CurrentUser };
        Assert.Equal(UninstallPlanner.NeedsAdminReason, ProgramsViewModel.SettingsReason(machine, needsAdmin));
        Assert.Contains("Benutzerkonto", ProgramsViewModel.SettingsReason(user, needsAdmin));
        Assert.DoesNotContain("ohne Adminrechte ändern", ProgramsViewModel.SettingsReason(user, needsAdmin));
        var other = new UserLaunchException(UninstallPlanner.NoUserLaunchReason, 5);
        Assert.Equal(UninstallPlanner.NoUserLaunchReason, ProgramsViewModel.SettingsReason(user, other));
    }

    [Fact]
    public async Task Search_and_sort()
    {
        var vm = await Loaded(
            P("Alpha", "Alpha Inc", 10L * 1024 * 1024, new DateTime(2020, 5, 1)),
            P("beta", "Beta Ltd", null, new DateTime(2024, 1, 2)),
            P("Gamma", "Alpha Soft", 2048, null));
        Assert.Equal("3 von 3 Programmen", vm.CountText);
        Assert.Equal(new[] { "Alpha", "beta", "Gamma" }, vm.Items.Select(i => i.Name));

        vm.SearchText = "alpha";
        Assert.Equal(new[] { "Alpha", "Gamma" }, vm.Items.Select(i => i.Name));
        Assert.Equal("2 von 3 Programmen", vm.CountText);

        vm.SearchText = "";
        vm.Sort = ProgramSort.Size;
        Assert.Equal(new[] { "Alpha", "Gamma", "beta" }, vm.Items.Select(i => i.Name));
        vm.Sort = ProgramSort.Date;
        Assert.Equal(new[] { "beta", "Alpha", "Gamma" }, vm.Items.Select(i => i.Name));

        var alpha = Row(vm, "Alpha");
        Assert.Equal("01.05.2020", alpha.DateText);
        Assert.Equal(Labels.Size(10L * 1024 * 1024), alpha.SizeText);
        Assert.Equal("", Row(vm, "beta").SizeText);
        Assert.Equal("", Row(vm, "Gamma").DateText);
    }

    [Fact]
    public async Task Flags_map_to_text_and_tone()
    {
        var vm = await Loaded(P("A", flag: ProgramFlag.Pua), P("B", flag: ProgramFlag.Miner), P("C"));
        Assert.Equal(("Unerwünscht", Tone.Watch), (Row(vm, "A").FlagText, Row(vm, "A").FlagTone));
        Assert.Equal(("Miner", Tone.Alarm), (Row(vm, "B").FlagText, Row(vm, "B").FlagTone));
        Assert.Equal((null, Tone.Neutral), (Row(vm, "C").FlagText, Row(vm, "C").FlagTone));
        Assert.Null(Row(vm, "C").ProtectedHint);
    }

    [Fact]
    public async Task Protected_row_has_hint_and_no_uninstall()
    {
        var vm = await Loaded(P("Lexware", flag: ProgramFlag.Protected));
        var row = Row(vm, "Lexware");
        Assert.False(row.CanUninstall);
        Assert.Equal("Geschützt", row.FlagText);
        Assert.Equal(Tone.Good, row.FlagTone);
        Assert.Equal("Geschützt – bei Bedarf über Windows-Einstellungen → Apps deinstallieren", row.ProtectedHint);
        Assert.False(vm.UninstallCommand.CanExecute(row));

        // even when called directly nothing is planned
        await vm.UninstallCommand.ExecuteAsync(row);
        Assert.Empty(_actions.Planned);
    }

    [Fact]
    public async Task WinWaechter_itself_is_treated_like_a_protected_entry()
    {
        var byName = P("WinWächter 0.3.0", publisher: null);
        var byPublisher = P("Some Tool", publisher: "Tahsin Yilmaz");
        var byFolder = new InstalledProgram(Hive.LocalMachine, Root, "Own_is1", "Renamed", "Other", "1.0", null, null,
            "\"" + Exe + "\"", "\"" + AppContext.BaseDirectory.TrimEnd('\\').ToUpperInvariant() + "\"", null, false, ProgramFlag.None);
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.OpenSettings, null, UninstallPlanner.MissingReason, true);
        var vm = await Loaded(byName, byPublisher, byFolder, P("Foo"));

        foreach (var row in vm.Items.Where(r => r.Name != "Foo"))
        {
            Assert.False(row.CanUninstall, row.Name);
            Assert.False(row.CanForceRemove, row.Name);
            Assert.Equal("Das ist WinWächter selbst – bitte über die Windows-Einstellungen deinstallieren.", row.ProtectedHint);
            Assert.False(vm.UninstallCommand.CanExecute(row));
            await vm.UninstallCommand.ExecuteAsync(row);
            Assert.False(vm.ForceRemoveCommand.CanExecute(row));
        }
        Assert.Empty(_actions.Planned);
        Assert.True(Row(vm, "Foo").CanUninstall);
        Assert.Null(Row(vm, "Foo").ProtectedHint);
    }

    [Fact]
    public async Task Settings_route_opens_appsfeatures_and_sets_pending()
    {
        var p = P("Foo");
        var vm = await Loaded(p);
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        var c = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Über Windows-Einstellungen deinstallieren", c.Title);
        Assert.Equal($"{UninstallPlanner.UnsafeReason} Bitte deinstallieren Sie „Foo“ über die Windows-Einstellungen.", c.Message);
        Assert.Equal("Einstellungen öffnen", c.ConfirmText);
        Assert.Equal(new[] { "ms-settings:appsfeatures" }, _shell.Links);
        Assert.Same(p, vm.Pending);
        Assert.Equal("Nach der Deinstallation von „Foo“ lassen sich Reste suchen.", vm.PendingText);
        Assert.Empty(_actions.Runs);
        Assert.False(Row(vm, "Foo").CanForceRemove);
    }

    [Fact]
    public async Task Declined_settings_route_opens_nothing()
    {
        _dialogs.ConfirmResult = _ => false;
        var vm = await Loaded(P("Foo"));
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));
        Assert.Empty(_shell.Links);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public async Task Force_offer_stays_when_settings_confirm_is_declined()
    {
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.OpenSettings, null, UninstallPlanner.MissingReason, true);
        _dialogs.ConfirmResult = _ => false;
        var vm = await Loaded(P("Foo"));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        Assert.Single(_dialogs.Confirms);
        Assert.Empty(_shell.Links);
        Assert.Null(vm.Pending);
        Assert.True(Row(vm, "Foo").CanForceRemove);
        Assert.True(vm.ForceRemoveCommand.CanExecute(Row(vm, "Foo")));
    }

    [Fact]
    public async Task Failed_leftover_search_keeps_pending()
    {
        var p = P("Foo");
        var vm = await Loaded(p);
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));   // settings route
        Assert.Same(p, vm.Pending);

        _actions.Programs = [];
        _actions.FindLeftoversThrows = new IOException("Registry nicht lesbar");
        await vm.FindLeftoversCommand.ExecuteAsync(null);

        Assert.Contains(_dialogs.Informs, i => i.Title == "Restesuche fehlgeschlagen");
        Assert.Same(p, vm.Pending);
        Assert.True(vm.FindLeftoversCommand.CanExecute(null));

        _actions.FindLeftoversThrows = null;
        await vm.FindLeftoversCommand.ExecuteAsync(null);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public async Task Uninstaller_starts_off_the_calling_thread()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _dialogs.AskResult = false;
        var vm = await Loaded(P("Foo"));
        using var ui = new UiThread();
        await ui.Run(() => vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo")));
        Assert.Single(_actions.Runs);
        Assert.NotEqual(ui.ThreadId, _actions.RunThreadId);
    }

    // a single thread with its own context, like the WPF dispatcher: awaits come back to it
    sealed class UiThread : SynchronizationContext, IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Call, object? State)> _queue = new();
        readonly Thread _thread;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (call, state) in _queue.GetConsumingEnumerable()) call(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task Run(Func<Task> work)
        {
            var done = new TaskCompletionSource();
            Post(async _ =>
            {
                try { await work(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task Missing_uninstaller_enables_force_remove()
    {
        _actions.PlanFor = _ => new UninstallPlan(UninstallRoute.OpenSettings, null, UninstallPlanner.MissingReason, true);
        var vm = await Loaded(P("Foo"), P("Msi", msi: true));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));
        Assert.True(Row(vm, "Foo").CanForceRemove);
        Assert.True(vm.ForceRemoveCommand.CanExecute(Row(vm, "Foo")));

        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Msi"));
        Assert.False(Row(vm, "Msi").CanForceRemove);
        Assert.False(vm.ForceRemoveCommand.CanExecute(Row(vm, "Msi")));

        // the offer survives a reload
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(Row(vm, "Foo").CanForceRemove);
    }

    [Fact]
    public async Task Launch_confirm_is_final_and_names_irreversibility()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _dialogs.ConfirmResult = _ => false;
        var vm = await Loaded(P("Foo"));
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        var c = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Deinstallieren", c.Title);
        Assert.True(c.Final);
        Assert.Equal("„Foo“ von Foo GmbH. Das Deinstallationsprogramm des Herstellers wird gestartet. " +
                     "Das lässt sich in WinWächter nicht rückgängig machen.", c.Message);
        Assert.Contains("lässt sich in WinWächter nicht rückgängig machen", c.Message);
        Assert.Equal(new[] { $"Programm: {Exe} /S", "Signatur: gültig, Herausgeber Foo GmbH" }, c.Items);
        Assert.Empty(_actions.Runs);
        Assert.Empty(_dialogs.Asks);
    }

    [Fact]
    public async Task Launch_names_unknown_publisher_and_unchecked_signature()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _actions.Signature = new SignatureInfo(SignatureState.Unsigned, null, false);
        _dialogs.ConfirmResult = _ => false;
        var vm = await Loaded(P("Foo", publisher: null));
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        var c = Assert.Single(_dialogs.Confirms);
        Assert.StartsWith("„Foo“ von unbekanntem Herausgeber.", c.Message);
        Assert.Equal("Signatur: nicht signiert", c.Items[1]);
    }

    [Fact]
    public async Task Cancelled_exit_offers_nothing()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _actions.ExitCode = 1602;
        var vm = await Loaded(P("Foo"));
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        Assert.Single(_actions.Runs);
        Assert.Empty(_dialogs.Asks);
        Assert.Empty(_actions.LeftoverCalls);
        Assert.False(vm.ShowLeftovers);
        Assert.Null(vm.Pending);
        Assert.Equal("Deinstallation abgebrochen.", vm.LastSummary);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.ProgressText);
    }

    [Fact]
    public async Task Running_uninstaller_shows_that_it_cannot_be_cancelled()
    {
        _actions.PlanFor = _ => LaunchPlan;
        var vm = await Loaded(P("Foo"));
        string? during = null;
        _dialogs.ConfirmResult = _ => true;
        _dialogs.AskResult = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressText) && vm.ProgressText == ProgramsViewModel.RunningText) during = vm.ProgressText;
        };
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));
        Assert.Equal(ProgramsViewModel.RunningText, during);
        Assert.Contains("nicht abbrechen", ProgramsViewModel.RunningText);
    }

    [Fact]
    public async Task Not_yet_answer_keeps_pending()
    {
        var p = P("Foo");
        _actions.PlanFor = _ => LaunchPlan;
        _dialogs.AskResult = false;
        var vm = await Loaded(p);
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        var ask = Assert.Single(_dialogs.Asks);
        Assert.Equal(("Deinstallation", "Ist die Deinstallation abgeschlossen?", "Ja", "Noch nicht"), ask);
        Assert.Same(p, vm.Pending);
        Assert.Empty(_actions.LeftoverCalls);
        Assert.True(vm.FindLeftoversCommand.CanExecute(null));
    }

    [Fact]
    public async Task Children_still_running_adds_a_note_to_the_done_question()
    {
        var p = P("Foo");
        _actions.PlanFor = _ => LaunchPlan;
        _actions.ChildrenStillRunning = true;
        _dialogs.AskResult = false;
        var vm = await Loaded(p);
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        var ask = Assert.Single(_dialogs.Asks);
        Assert.Equal(("Deinstallation",
            "Das Deinstallationsprogramm hat weitere Programme geöffnet (z. B. Browser). Ist die Deinstallation abgeschlossen?",
            "Ja", "Noch nicht"), ask);
        Assert.Same(p, vm.Pending);
        Assert.False(vm.IsBusy);
    }

    Finding FolderLeftover() => new("leftover|folder", FindingArea.Speicher, "Foo", @"C:\Program Files\Foo",
        ["Programmordner nach der Deinstallation"], Rating.Info, false,
        new OkChange("folder", @"C:\Program Files\Foo", "Ordner „C:\\Program Files\\Foo“ in die Ablage"));

    Finding TaskLeftover() => new("leftover|task", FindingArea.Aufgabe, "Foo Updater", @"\Foo Updater",
        ["Ziel fehlt"], Rating.Entbehrlich, true, new OkChange("task", @"\Foo Updater", "Aufgabe „Foo Updater“ deaktivieren"));

    [Fact]
    public async Task Finished_and_gone_shows_leftovers_panel()
    {
        var p = P("Foo");
        _actions.PlanFor = _ => LaunchPlan;
        _actions.Leftovers = [FolderLeftover(), TaskLeftover()];
        var vm = await Loaded(p);
        var applied = 0;
        vm.ChangesApplied += (_, _) => applied++;
        _actions.Programs = [];   // the uninstaller removed the entry
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        Assert.Single(_dialogs.Asks);
        Assert.Same(p, Assert.Single(_actions.LeftoverCalls));
        Assert.Empty(vm.Items);
        Assert.True(vm.ShowLeftovers);
        Assert.Equal("Reste von „Foo“", vm.LeftoversTitle);
        Assert.Equal(2, vm.Leftovers.Count);
        Assert.False(vm.Leftovers[0].IsSelected);
        Assert.True(vm.Leftovers[1].IsSelected);

        await vm.ApplyLeftoversCommand.ExecuteAsync(null);
        var session = _store.List()[0];
        Assert.Equal("Reste von Foo", session.Title);
        var rec = Assert.Single(session.Records);
        Assert.Equal(@"\Foo Updater", rec.Target);
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal(1, applied);
        // the unselected folder stays offered
        var left = Assert.Single(vm.Leftovers);
        Assert.Equal("leftover|folder", left.Finding.Id);
        Assert.True(vm.ShowLeftovers);
        Assert.False(vm.ApplyLeftoversCommand.CanExecute(null));
    }

    [Fact]
    public async Task Finished_and_gone_without_leftovers_says_so()
    {
        _actions.PlanFor = _ => LaunchPlan;
        var vm = await Loaded(P("Foo"));
        _actions.Programs = [];
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        Assert.False(vm.ShowLeftovers);
        Assert.Contains(_dialogs.Informs, i => i.Title == "Reste" && i.Message == "Keine Reste gefunden.");
    }

    [Fact]
    public async Task Finished_but_still_listed_offers_force_remove()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _actions.StillListed = _ => true;
        var vm = await Loaded(P("Foo"));
        var before = _actions.ReadCount;
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));

        Assert.True(_actions.ReadCount > before);
        Assert.True(Row(vm, "Foo").CanForceRemove);
        Assert.Contains(_dialogs.Informs, i => i.Message.StartsWith("Der Eintrag ist noch vorhanden."));
        Assert.Empty(_actions.LeftoverCalls);
    }

    [Fact]
    public async Task Finished_but_still_listed_msi_gets_no_force_remove()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _actions.StillListed = _ => true;
        var vm = await Loaded(P("Foo", msi: true));
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));
        Assert.False(Row(vm, "Foo").CanForceRemove);
    }

    [Fact]
    public async Task Restart_hint_for_3010()
    {
        _actions.PlanFor = _ => LaunchPlan;
        _actions.ExitCode = 3010;
        var vm = await Loaded(P("Foo"));
        _actions.Programs = [];
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));
        Assert.Contains("Ein Neustart ist nötig, um die Deinstallation abzuschließen.", vm.LastSummary);
    }

    [Fact]
    public async Task Find_leftovers_for_pending_program()
    {
        var p = P("Foo");
        _actions.Leftovers = [TaskLeftover()];
        _actions.StillListed = _ => true;
        var vm = await Loaded(p);
        await vm.UninstallCommand.ExecuteAsync(Row(vm, "Foo"));   // settings route
        Assert.Same(p, vm.Pending);

        await vm.FindLeftoversCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Informs, i =>
            i.Message == "Das Programm ist noch installiert. Reste lassen sich erst nach der Deinstallation suchen.");
        Assert.Same(p, vm.Pending);
        Assert.Empty(_actions.LeftoverCalls);

        _actions.StillListed = _ => false;
        _actions.Programs = [];
        await vm.FindLeftoversCommand.ExecuteAsync(null);
        Assert.Null(vm.Pending);
        Assert.True(vm.ShowLeftovers);
        Assert.Single(vm.Leftovers);

        vm.CloseLeftoversCommand.Execute(null);
        Assert.False(vm.ShowLeftovers);
        Assert.Empty(vm.Leftovers);
    }

    [Fact]
    public async Task Force_remove_previews_every_change_and_journals()
    {
        const string note = "Ordner nicht angeboten: Der Ordner existiert nicht mehr.";
        _actions.ForcedFor = p => new ForcedRemovalPlan(p,
            [new OkChange("task", @"\Foo", "A"), new OkChange("regkey", @"HKLM\" + Root + @"\Foo_is1", "B")], null, [note]);
        var vm = await Loaded(P("Foo"));
        var applied = 0;
        vm.ChangesApplied += (_, _) => applied++;
        var row = Row(vm, "Foo");
        row.CanForceRemove = true;

        await vm.ForceRemoveCommand.ExecuteAsync(row);

        var c = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Erzwungen entfernen", c.Title);
        Assert.Equal("Alles wird gesichert und lässt sich im Verlauf rückgängig machen.", c.Message);
        Assert.Equal("Entfernen", c.ConfirmText);
        Assert.False(c.Final);
        Assert.Equal(new[] { "A", "B", note }, c.Items);

        var session = _store.List()[0];
        Assert.Equal("Erzwungen entfernt: Foo", session.Title);
        Assert.Equal(2, session.Records.Count);
        Assert.All(session.Records, r => Assert.Equal(ChangeStatus.Applied, r.Status));
        Assert.Equal(1, applied);
        Assert.Contains(_dialogs.Informs, i => i.Title == "Zusammenfassung");
    }

    [Fact]
    public async Task Force_remove_without_changes_only_informs()
    {
        _actions.ForcedFor = p => new ForcedRemovalPlan(p, [], null, [ForcedRemovalPlanner.EntryGone]);
        var vm = await Loaded(P("Foo"));
        var row = Row(vm, "Foo");
        row.CanForceRemove = true;
        await vm.ForceRemoveCommand.ExecuteAsync(row);

        Assert.Empty(_dialogs.Confirms);
        var info = Assert.Single(_dialogs.Informs);
        Assert.Equal(new[] { ForcedRemovalPlanner.EntryGone }, info.Items);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Declined_force_remove_changes_nothing()
    {
        _actions.ForcedFor = p => new ForcedRemovalPlan(p, [new OkChange("task", @"\Foo", "A")], null, []);
        _dialogs.ConfirmResult = _ => false;
        var vm = await Loaded(P("Foo"));
        var row = Row(vm, "Foo");
        row.CanForceRemove = true;
        await vm.ForceRemoveCommand.ExecuteAsync(row);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Actions_disabled_without_admin()
    {
        _actions.Programs = [P("Foo")];
        _actions.PlanFor = _ => LaunchPlan;

        // the same staged page (pending program, leftover panel with a selected entry, force offer) with and without admin
        async Task<(ProgramsViewModel Vm, ProgramItemViewModel Row)> Staged(bool canChange)
        {
            var staged = Vm(canChange);
            await staged.RefreshCommand.ExecuteAsync(null);
            var r = Row(staged, "Foo");
            r.CanForceRemove = true;
            staged.Pending = r.Program;
            staged.Leftovers.Add(new FindingItemViewModel(TaskLeftover()));
            return (staged, r);
        }

        var (admin, adminRow) = await Staged(true);
        Assert.Equal(1, admin.SelectedLeftoverCount);
        Assert.True(admin.UninstallCommand.CanExecute(adminRow));
        Assert.True(admin.ForceRemoveCommand.CanExecute(adminRow));
        Assert.True(admin.FindLeftoversCommand.CanExecute(null));
        Assert.True(admin.ApplyLeftoversCommand.CanExecute(null));

        var (vm, row) = await Staged(false);
        Assert.Equal(1, vm.SelectedLeftoverCount);
        Assert.True(vm.ShowAdminNotice);
        Assert.Equal(AppEnvironment.AdminNotice, vm.AdminNotice);
        Assert.False(vm.UninstallCommand.CanExecute(row));
        Assert.False(vm.ForceRemoveCommand.CanExecute(row));
        Assert.False(vm.FindLeftoversCommand.CanExecute(null));
        Assert.False(vm.ApplyLeftoversCommand.CanExecute(null));

        await vm.UninstallCommand.ExecuteAsync(row);
        await vm.ForceRemoveCommand.ExecuteAsync(row);
        Assert.Empty(_actions.Planned);
        Assert.Empty(_actions.ForcedCalls);
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public async Task Busy_gate_blocks_actions()
    {
        _actions.PlanFor = _ => LaunchPlan;
        var vm = await Loaded(P("Foo"));
        var row = Row(vm, "Foo");
        var release = new TaskCompletionSource();
        var held = _gate.RunAsync(new object(), () => release.Task);
        Assert.False(vm.UninstallCommand.CanExecute(row));
        Assert.Equal(ChangeGate.BusyHint, vm.GateHint);
        release.SetResult();
        await held;
        Assert.True(vm.UninstallCommand.CanExecute(row));
    }

    sealed class NoUpdates : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Launch(VerifiedUpdate update) { }
    }

    [Fact]
    public async Task Shell_first_visit_reads_privacy_and_programme_once()
    {
        _actions.Programs = [P("Foo")];
        var optimize = new OptimizeViewModel(_engine, _dialogs, canChange: false, _gate);
        var protect = new ProtectViewModel(new FakeDefender(), (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]),
            _engine, _ => 0, _dialogs, canChange: false, _gate);
        var security = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<WinWaechter.Core.Security.Checks.SecurityItem>>([]),
            _engine, new FakeDefender(), _dialogs, _shell, canChange: false, _gate);
        var privacy = new PrivacyViewModel(_ => Task.FromResult(new PrivacyScanner(_reg, RuleSet.Default).Scan(_ => true)),
            _engine, _dialogs, canChange: false, _gate);
        var programs = Vm(canChange: false);
        var history = new HistoryViewModel(_store, _engine, new AblageJanitor(_store), _dialogs, canChange: false, _gate);
        var update = new UpdateViewModel(new NoUpdates(), _dialogs, canInstall: false, () => { }, _gate);
        var settings = new SettingsViewModel(new AppSettings(), new AppEnvironment(false, _root, new SemVer(0, 3, 0)), update, _shell,
            new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")));
        var shell = new ShellViewModel(optimize, protect, security, privacy, programs, history, settings, update, isElevated: false);

        Assert.Same(privacy, shell.PrimaryNav[4].Page);
        var nav = shell.PrimaryNav[5];
        Assert.Equal("", nav.Glyph);
        Assert.Same(programs, nav.Page);

        // first visit of each page reads its data
        shell.PrimaryNav[4].IsSelected = true;
        if (privacy.RefreshCommand.ExecutionTask is { } t1) await t1;
        Assert.NotEmpty(privacy.Rows);

        nav.IsSelected = true;
        Assert.Same(programs, shell.CurrentPage);
        if (programs.RefreshCommand.ExecutionTask is { } t2) await t2;
        Assert.Single(programs.Items);
        Assert.Equal(1, _actions.ReadCount);

        // a second visit does not read again
        shell.PrimaryNav[0].IsSelected = true;
        nav.IsSelected = true;
        Assert.Equal(1, _actions.ReadCount);
    }
}
