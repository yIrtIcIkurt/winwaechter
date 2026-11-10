using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SecurityViewModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-secvm-" + Guid.NewGuid().ToString("N"));
    readonly ChangeGate _gate = new();
    readonly Dialogs _dialogs = new();
    readonly RecordingShell _shell = new();
    readonly FakeFirewall _fw = new();
    readonly FakeDefender _defender = new();
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;

    public SecurityViewModelTests()
    {
        _store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(new FakeRegistry(), new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), RuleSet.Default);
        FirewallChange.Register(factory, _fw);
        _engine = new OptimizerEngine([], _store, factory, _ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Dialogs : IDialogService
    {
        public bool Answer { get; set; } = true;
        public List<(string Title, string Message, string ConfirmText, IReadOnlyList<string> Items)> Confirms { get; } = [];
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            Confirms.Add((title, message, confirmText, items ?? []));
            return Answer;
        }
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    sealed class RecordingShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    sealed class NoUpdates : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Launch(VerifiedUpdate update) { }
    }

    static SecurityItem Row(string id, Light light, string title = "Titel", IReadOnlyList<SecurityItem>? children = null) =>
        new(id, title, light, "Befund", "Erklärung", Children: children);

    // Red, Yellow, Yellow, Grey, Green (with a yellow child, which must not be counted)
    static IReadOnlyList<SecurityItem> Mixed() =>
    [
        Row("a", Light.Red), Row("b", Light.Yellow), Row("c", Light.Yellow), Row("d", Light.Grey),
        Row("e", Light.Green, children: [Row("e1", Light.Yellow)]),
    ];

    SecurityViewModel Vm(IReadOnlyList<SecurityItem> items, bool canChange = true) =>
        new(_ => Task.FromResult(items), _engine, _defender, _dialogs, _shell, canChange, _gate);

    [Fact]
    public async Task Counts_and_tile_text()
    {
        var vm = Vm(Mixed());
        Assert.False(vm.IsLoaded);
        Assert.Equal("–", vm.TileText);
        var raised = 0;
        vm.StateChanged += (_, _) => raised++;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsLoaded);
        Assert.Equal(1, vm.RedCount);
        Assert.Equal(2, vm.YellowCount);
        Assert.Equal("1 rot · 2 gelb", vm.TileText);
        Assert.Equal(Tone.Alarm, vm.TileTone);
        Assert.Equal(1, raised);

        var green = Vm([Row("a", Light.Green), Row("b", Light.Green)]);
        await green.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Alles in Ordnung", green.TileText);
        Assert.Equal(Tone.Good, green.TileTone);

        var yellow = Vm([Row("a", Light.Yellow)]);
        await yellow.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(Tone.Watch, yellow.TileTone);
    }

    [Fact]
    public async Task Failed_check_keeps_state_unloaded_and_reports()
    {
        var vm = new SecurityViewModel(_ => throw new InvalidOperationException("kaputt"), _engine, _defender, _dialogs, _shell, true, _gate);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsLoaded);
        Assert.Equal("–", vm.TileText);
        Assert.Contains("kaputt", vm.LastSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Light_texts_and_tones()
    {
        var cases = new[]
        {
            (Light.Green, Tone.Good, "In Ordnung"), (Light.Yellow, Tone.Watch, "Prüfen"),
            (Light.Red, Tone.Alarm, "Handlungsbedarf"), (Light.Grey, Tone.Neutral, "Nicht prüfbar"),
        };
        foreach (var (light, tone, text) in cases)
        {
            var row = new SecurityItemViewModel(Row("x", light));
            Assert.Equal(tone, row.LightTone);
            Assert.Equal(text, row.LightText);
        }
        var parent = new SecurityItemViewModel(Row("p", Light.Yellow, children: [Row("c", Light.Grey)]));
        Assert.Single(parent.Children);
        Assert.Equal("Nicht prüfbar", parent.Children[0].LightText);
        Assert.Equal("Erklärung", parent.Children[0].Explanation);
    }

    [Fact]
    public async Task Link_action_opens_only_allowed_links()
    {
        var good = new SecurityItem("u", "Updates", Light.Yellow, "f", "e", SecurityActionKind.Link, "Windows Update öffnen", SystemLinks.WindowsUpdate);
        var evil = new SecurityItem("v", "Böse", Light.Yellow, "f", "e", SecurityActionKind.Link, "Öffnen", "https://evil.com");
        var vm = Vm([good, evil]);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal(["ms-settings:windowsupdate"], _shell.Links);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[1]);
        Assert.Single(_shell.Links);
    }

    [Fact]
    public async Task Firewall_change_is_journalled_and_undoable()
    {
        _fw.State[FirewallProfile.Private] = false;
        var items = SecurityChecks.Firewall(_fw, []);
        var vm = Vm(items);
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Items.Single(i => i.Item.Id == SecurityIds.FirewallPrivate);
        Assert.Equal("Einschalten", row.ActionLabel);
        var applied = 0;
        vm.ChangesApplied += (_, _) => applied++;

        await vm.RunActionCommand.ExecuteAsync(row);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Equal("Firewall einschalten", confirm.Title);
        Assert.Equal("Die Änderung steht danach im Verlauf und lässt sich dort rückgängig machen.", confirm.Message);
        Assert.Equal("Einschalten", confirm.ConfirmText);
        Assert.Equal(["Firewall für das Profil „Privat“ einschalten"], confirm.Items);
        Assert.True(_fw.State[FirewallProfile.Private]);
        Assert.Equal(1, applied);

        var session = _store.List()[0];
        Assert.Equal("Sicherheit", session.Title);
        var record = Assert.Single(session.Records);
        Assert.Equal("firewall", record.Kind);

        Assert.False(vm.IsBusy);

        _engine.UndoSession(session);
        Assert.False(_fw.State[FirewallProfile.Private]);
    }

    [Fact]
    public async Task Declined_confirmation_changes_nothing()
    {
        _fw.State[FirewallProfile.Public] = false;
        _dialogs.Answer = false;
        var vm = Vm(SecurityChecks.Firewall(_fw, []));
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RunActionCommand.ExecuteAsync(vm.Items.Single(i => i.Item.Id == SecurityIds.FirewallPublic));
        Assert.False(_fw.State[FirewallProfile.Public]);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Defender_scan_action_calls_scan_path()
    {
        const string path = @"C:\Users\x\AppData\Roaming\tool.exe";
        var child = new SecurityItem("l1", "tool.exe", Light.Yellow, "Port 4444", "e", SecurityActionKind.DefenderScan,
            "Von Defender prüfen lassen", ScanPath: path);
        var vm = Vm([Row("listeners", Light.Yellow, children: [child])]);
        await vm.RefreshCommand.ExecuteAsync(null);

        var childRow = vm.Items[0].Children[0];
        Assert.True(vm.RunActionCommand.CanExecute(childRow));
        await vm.RunActionCommand.ExecuteAsync(childRow);

        Assert.Equal([path], _defender.ScannedPaths);
        Assert.Contains("geprüft", vm.LastSummary);
    }

    [Fact]
    public async Task Change_disabled_without_admin()
    {
        _fw.State[FirewallProfile.Domain] = false;
        var link = new SecurityItem("u", "Updates", Light.Yellow, "f", "e", SecurityActionKind.Link, "Windows Update öffnen", SystemLinks.WindowsUpdate);
        var vm = Vm([.. SecurityChecks.Firewall(_fw, []), link], canChange: false);
        await vm.RefreshCommand.ExecuteAsync(null);

        var fw = vm.Items.Single(i => i.Item.Id == SecurityIds.FirewallDomain);
        Assert.False(vm.RunActionCommand.CanExecute(fw));
        Assert.True(vm.RunActionCommand.CanExecute(vm.Items.Last()));
        Assert.True(vm.ShowAdminNotice);
    }

    [Fact]
    public async Task Change_disabled_while_another_page_holds_the_gate()
    {
        _fw.State[FirewallProfile.Domain] = false;
        var vm = Vm(SecurityChecks.Firewall(_fw, []));
        await vm.RefreshCommand.ExecuteAsync(null);
        var fw = vm.Items.Single(i => i.Item.Id == SecurityIds.FirewallDomain);
        var release = new TaskCompletionSource();
        var held = _gate.RunAsync(new object(), () => release.Task);
        Assert.False(vm.RunActionCommand.CanExecute(fw));
        Assert.Equal(ChangeGate.BusyHint, vm.GateHint);
        release.SetResult();
        await held;
        Assert.True(vm.RunActionCommand.CanExecute(fw));
    }

    ShellViewModel Shell(SecurityViewModel security)
    {
        var optimize = new OptimizeViewModel(_engine, _dialogs, canChange: false, _gate);
        var protect = new ProtectViewModel(_defender, (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]),
            _engine, _ => 0, _dialogs, canChange: false, _gate);
        var privacy = new PrivacyViewModel(_ => Task.FromResult(new PrivacyScanner(new FakeRegistry(), RuleSet.Default).Scan(_ => true)),
            _engine, _dialogs, canChange: false, _gate);
        var programs = new ProgramsViewModel(new FakeProgramActions(), _engine, _dialogs, _shell, canChange: false, _gate);
        var history = new HistoryViewModel(_store, _engine, new AblageJanitor(_store), _dialogs, canChange: false, _gate);
        var update = new UpdateViewModel(new NoUpdates(), _dialogs, canInstall: false, () => { }, _gate);
        var settings = new SettingsViewModel(new AppSettings(), new AppEnvironment(false, _root, new SemVer(0, 3, 0)), update, _shell);
        return new ShellViewModel(optimize, protect, security, privacy, programs, history, settings, update, isElevated: false);
    }

    [Fact]
    public async Task Overview_shows_security_tile()
    {
        var vm = Vm(Mixed());
        var shell = Shell(vm);
        Assert.Equal("–", shell.Overview.SecurityText);
        var raised = new List<string?>();
        shell.Overview.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("1 rot · 2 gelb", shell.Overview.SecurityText);
        Assert.Equal(Tone.Alarm, shell.Overview.SecurityTone);
        Assert.Contains(nameof(OverviewViewModel.SecurityText), raised);
    }

    [Fact]
    public async Task Shell_nav_full_order()
    {
        var vm = Vm(Mixed());
        var shell = Shell(vm);

        Assert.Equal(new[] { "Übersicht", "Optimieren", "Schutz", "Sicherheit", "Datenschutz", "Programme", "Verlauf" },
            shell.PrimaryNav.Select(n => n.Title));
        var nav = shell.PrimaryNav[3];
        Assert.Equal("\uE72E", nav.Glyph);
        Assert.Same(vm, nav.Page);

        // the first visit reads the check when startup did not
        nav.IsSelected = true;
        Assert.Same(vm, shell.CurrentPage);
        if (vm.RefreshCommand.ExecutionTask is { } t) await t;
        Assert.True(vm.IsLoaded);
    }
}
