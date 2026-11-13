using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
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
    readonly FakeRegistry _reg = new();
    readonly FakeFirewallRules _fwRules = new();
    readonly FakeServiceControl _sc = new();
    readonly FakeRestoreConfig _restore = new();
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;

    public SecurityViewModelTests()
    {
        _store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(_reg, _sc, new FakeTaskStore(), new FakeFileOps(), RuleSet.Default);
        FirewallChange.Register(factory, _fw);
        SecValueChange.Register(factory, _reg);
        FirewallRuleChange.Register(factory, _fwRules, WindowsDir);
        SystemProtectChange.Register(factory, _sc, _restore);
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
        public List<(string Title, string Message)> Informs { get; } = [];
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) => Informs.Add((title, message));
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

    const string WindowsDir = @"C:\Windows";
    const string ToolPath = @"C:\Users\x\AppData\Roaming\tool.exe";
    const string ExplorerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";

    // a view model that runs one-off actions through the shared runner; rows() is read on every check
    SecurityViewModel Fixer(Func<IReadOnlyList<SecurityItem>> rows, RecordingDialogs dialogs, bool canChange = true) =>
        new(_ => Task.FromResult(rows()), _engine, _defender, dialogs, _shell, canChange, _gate,
            actions: new ActionRunner(dialogs, _gate));

    SecurityItem ListenerChild() =>
        new(SecurityIds.Listeners, "tool.exe", Light.Yellow, "Port 4444", "Nicht signiert.", SecurityActionKind.DefenderScan,
            "Von Defender prüfen lassen", Change: new FirewallRuleChange(_fwRules, ToolPath, WindowsDir), ScanPath: ToolPath,
            ChangeLabel: "Eingehende Verbindungen sperren");

    SecurityItem DefenderRow() => SecurityChecks.Defender(_defender.GetStatusAsync().GetAwaiter().GetResult(), [],
        SecurityActionSet.Create(_defender, new FakeUpdateInstaller(), new FakeRestarter()));

    [Fact]
    public async Task Fix_button_applies_change_and_journals()
    {
        _reg.Seed(Hive.LocalMachine, ExplorerKey, "SmartScreenEnabled", "Off");
        var dialogs = new RecordingDialogs();
        var vm = Fixer(() => [SecurityChecks.SmartScreen(_reg)], dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Items[0];
        Assert.Equal("Einschalten", row.ActionLabel);
        Assert.False(row.HasChangeButton);
        Assert.True(vm.RunActionCommand.CanExecute(row));
        var applied = 0;
        vm.ChangesApplied += (_, _) => applied++;

        await vm.RunActionCommand.ExecuteAsync(row);

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Equal((SecurityViewModel.ConfirmNote, "Einschalten"), (confirm.Message, confirm.Button));
        Assert.Equal(["SmartScreen einschalten (warnen)"], confirm.Items);
        Assert.Equal("Warn", _reg.Get(Hive.LocalMachine, ExplorerKey, "SmartScreenEnabled"));
        var session = Assert.Single(_store.List());
        Assert.Equal("Sicherheit", session.Title);
        Assert.Equal("secvalue", Assert.Single(session.Records).Kind);
        Assert.Equal(1, applied);
        // the page reads the check again and shows the new state
        Assert.Equal("Eingeschaltet", vm.Items[0].Finding);
        Assert.Equal("Zuletzt: 1 angewendet.", vm.LastSummary);
    }

    [Fact]
    public async Task Child_row_has_scan_and_block_buttons()
    {
        var dialogs = new RecordingDialogs();
        var vm = Fixer(() => [Row(SecurityIds.Listeners, Light.Yellow, children: [ListenerChild()])], dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        var child = vm.Items[0].Children[0];
        Assert.False(vm.Items[0].HasChangeButton);
        Assert.Null(vm.Items[0].ChangeLabel);
        Assert.True(child.HasAction && child.HasChangeButton);
        Assert.Equal(("Von Defender prüfen lassen", "Eingehende Verbindungen sperren"), (child.ActionLabel, child.ChangeLabel));
        Assert.True(vm.RunActionCommand.CanExecute(child));
        Assert.True(vm.RunChangeCommand.CanExecute(child));

        await vm.RunChangeCommand.ExecuteAsync(child);

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Equal(("Eingehende Verbindungen sperren", SecurityViewModel.ConfirmNote, "Eingehende Verbindungen sperren"),
            (confirm.Title, confirm.Message, confirm.Button));
        Assert.Equal(["Eingehende Verbindungen für „tool.exe“ sperren"], confirm.Items);
        Assert.Equal(["WinWächter: tool"], _fwRules.Added);
        Assert.Empty(_defender.ScannedPaths);
        var session = Assert.Single(_store.List());
        Assert.Equal("fwrule", Assert.Single(session.Records).Kind);

        // the scan button stays a scan
        await vm.RunActionCommand.ExecuteAsync(vm.Items[0].Children[0]);
        Assert.Equal([ToolPath], _defender.ScannedPaths);
        Assert.Single(_store.List());
    }

    [Fact]
    public async Task Action_row_runs_through_runner_and_refreshes()
    {
        _defender.SignatureAgeDays = 10;
        var dialogs = new RecordingDialogs();
        var checks = 0;
        var vm = Fixer(() => { checks++; return [DefenderRow()]; }, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Items[0];
        Assert.Equal("Jetzt aktualisieren", row.ActionLabel);
        Assert.True(vm.RunActionCommand.CanExecute(row));
        var applied = 0;
        vm.ChangesApplied += (_, _) => applied++;

        await vm.RunActionCommand.ExecuteAsync(row);

        // harmless: the click is enough, no confirmation
        Assert.Empty(dialogs.Confirms);
        Assert.Contains("update", _defender.Calls);
        Assert.Equal(2, checks);
        Assert.Equal("Die Virendefinitionen sind aktuell.", vm.LastSummary);
        Assert.False(vm.IsBusy);
        Assert.False(_gate.IsBusy);
        // nothing journalled, so the history does not reload
        Assert.Equal(0, applied);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Declined_action_runs_nothing()
    {
        _defender.RealTime = false;
        var dialogs = new RecordingDialogs { ConfirmAnswer = false };
        var checks = 0;
        var vm = Fixer(() => { checks++; return [DefenderRow()]; }, dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[0]);

        Assert.True(Assert.Single(dialogs.Confirms).Final);
        Assert.DoesNotContain("enable-realtime", _defender.Calls);
        Assert.Equal(1, checks);
        Assert.Null(vm.LastSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Realtime_blocked_offers_windows_security()
    {
        _defender.RealTime = false;
        _defender.EnableBlocked = true;
        var dialogs = new RecordingDialogs();
        var vm = Fixer(() => [DefenderRow()], dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Einschalten", vm.Items[0].ActionLabel);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal(2, dialogs.Confirms.Count);
        Assert.EndsWith(ActionRunner.FinalNote, dialogs.Confirms[0].Message);
        var offer = dialogs.Confirms[1];
        Assert.Equal(("Echtzeitschutz einschalten", SecurityActionSet.RealTimeBlocked, "Windows-Sicherheit öffnen"),
            (offer.Title, offer.Message, offer.Button));
        Assert.Equal([SystemLinks.DefenderThreat], _shell.Links);
        Assert.Equal(SecurityActionSet.RealTimeBlocked, vm.LastSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Fixes_locked_without_admin()
    {
        _defender.SignatureAgeDays = 10;
        _reg.Seed(Hive.LocalMachine, ExplorerKey, "SmartScreenEnabled", "Off");
        var link = new SecurityItem("u", "Updates", Light.Yellow, "f", "e", SecurityActionKind.Link, "Windows Update öffnen", SystemLinks.WindowsUpdate);
        var dialogs = new RecordingDialogs();
        var vm = Fixer(() => [SecurityChecks.SmartScreen(_reg), DefenderRow(),
            Row(SecurityIds.Listeners, Light.Yellow, children: [ListenerChild()]), link], dialogs, canChange: false);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.RunActionCommand.CanExecute(vm.Items[0]));
        Assert.False(vm.RunActionCommand.CanExecute(vm.Items[1]));
        Assert.False(vm.RunChangeCommand.CanExecute(vm.Items[2].Children[0]));
        Assert.True(vm.RunActionCommand.CanExecute(vm.Items[3]));
        await vm.RunChangeCommand.ExecuteAsync(vm.Items[2].Children[0]);
        await vm.RunActionCommand.ExecuteAsync(vm.Items[1]);
        Assert.Empty(dialogs.Confirms);
        Assert.Empty(_fwRules.Added);
        Assert.DoesNotContain("update", _defender.Calls);

        // without the shared runner an action is never offered
        var noRunner = Vm([DefenderRow()]);
        await noRunner.RefreshCommand.ExecuteAsync(null);
        Assert.False(noRunner.RunActionCommand.CanExecute(noRunner.Items[0]));
    }

    [Fact]
    public async Task Fix_buttons_wait_for_the_gate()
    {
        _defender.SignatureAgeDays = 10;
        var vm = Fixer(() => [Row(SecurityIds.Listeners, Light.Yellow, children: [ListenerChild()]), DefenderRow()], new RecordingDialogs());
        await vm.RefreshCommand.ExecuteAsync(null);
        var child = vm.Items[0].Children[0];
        var action = vm.Items[1];
        var changes = 0;
        vm.RunChangeCommand.CanExecuteChanged += (_, _) => changes++;
        var release = new TaskCompletionSource();
        var held = _gate.RunAsync(new object(), () => release.Task);
        Assert.False(vm.RunChangeCommand.CanExecute(child));
        Assert.False(vm.RunActionCommand.CanExecute(action));
        release.SetResult();
        await held;
        Assert.True(vm.RunChangeCommand.CanExecute(child));
        Assert.True(vm.RunActionCommand.CanExecute(action));
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Sysprotect_confirm_says_partial()
    {
        _restore.State = false;
        var dialogs = new RecordingDialogs { ConfirmAnswer = false };
        var vm = Fixer(() => [SystemProtection.Check(_sc, _restore, _reg, new FakePolicyOwnership(), @"C:\")], dialogs);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Einschalten", vm.Items[0].ActionLabel);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[0]);

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Equal([@"Computerschutz für C:\ einschalten (Nur teilweise rückgängig)"], confirm.Items);
        Assert.Equal(SecurityViewModel.ConfirmNote + " " + SecurityViewModel.SysprotectNote, confirm.Message);
        Assert.Equal("Den Computerschutz schaltet WinWächter auch beim Rückgängigmachen nicht wieder ab, weil Windows dabei alle " +
            "Wiederherstellungspunkte löscht.", SecurityViewModel.SysprotectNote);
        Assert.Empty(_restore.EnableCalls);
        Assert.Empty(_store.List());
    }

    static SecurityItem Row(string id, Light light, string title = "Titel", IReadOnlyList<SecurityItem>? children = null) =>
        new(id, title, light, "Befund", "Erklärung", Children: children);

    // Red, Yellow, Yellow, Grey, Green (with a yellow child, which must not be counted)
    static IReadOnlyList<SecurityItem> Mixed() =>
    [
        Row("a", Light.Red), Row("b", Light.Yellow), Row("c", Light.Yellow), Row("d", Light.Grey),
        Row("e", Light.Green, children: [Row("e1", Light.Yellow)]),
    ];

    SecurityViewModel Vm(IReadOnlyList<SecurityItem> items, bool canChange = true, Func<bool>? defenderBusy = null) =>
        new(_ => Task.FromResult(items), _engine, _defender, _dialogs, _shell, canChange, _gate, defenderBusy);

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

    sealed class FakePages : IExtensionPages
    {
        public List<string> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public ManualResetEventSlim? Hold { get; set; }
        public string Address(string browser, string id) => $"edge://extensions/?id={id}";
        public void Open(string browser)
        {
            Hold?.Wait(TimeSpan.FromSeconds(10));
            if (Throw is not null) throw Throw;
            Calls.Add(browser);
        }
    }

    [Fact]
    public async Task Remove_action_opens_the_browser_page_and_says_what_to_do()
    {
        var id = new string('a', 32);
        var ext = new BrowserExtension("Edge", "Default", id, "Fremd", ExtensionOrigin.Unknown) { Permissions = [], Hosts = [] };
        var off = new BrowserExtension("Edge", "Default", new string('b', 32), "Aus", ExtensionOrigin.Store) { Disabled = true };
        var item = SecurityChecks.Extensions([ext, off], _ => true);
        var pages = new FakePages();
        var copied = new List<string>();
        var vm = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<SecurityItem>>([item]), _engine, _defender, _dialogs, _shell,
            false, _gate, null, pages, copied.Add);
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Items[0].Children[0];
        Assert.Equal(("Ansehen", Tone.Watch, "In Edge entfernen"), (row.LightText, row.LightTone, row.ActionLabel));
        Assert.Equal(("Deaktiviert", Tone.Calm), (vm.Items[0].Children[1].LightText, vm.Items[0].Children[1].LightTone));
        Assert.Equal(("Ansehen", Tone.Watch), (vm.Items[0].LightText, vm.Items[0].LightTone));
        Assert.Equal("In Ordnung", new SecurityItemViewModel(SecurityChecks.Extensions([off])).LightText);
        // opening a page changes nothing on the system, so it needs no admin rights
        Assert.True(vm.RunActionCommand.CanExecute(row));

        var address = $"edge://extensions/?id={id}";

        // declined: nothing copied, nothing started
        _dialogs.Answer = false;
        await vm.RunActionCommand.ExecuteAsync(row);
        var ask = Assert.Single(_dialogs.Confirms);
        Assert.Equal(("In Edge entfernen", SecurityViewModel.HowToText("Edge"), "Edge öffnen", address),
            (ask.Title, ask.Message, ask.ConfirmText, Assert.Single(ask.Items)));
        Assert.Empty(copied);
        Assert.Empty(pages.Calls);

        // Edge ignores edge:// pages from other programs: the address is copied and Edge opened in a new window
        _dialogs.Answer = true;
        await vm.RunActionCommand.ExecuteAsync(row);
        Assert.Equal([address], copied);
        Assert.Equal(["Edge"], pages.Calls);
        Assert.Equal("Edge ist geöffnet, die Adresse ist kopiert: Strg+L, Strg+V und Enter, dann „Entfernen“. Danach „Erneut prüfen“.",
            vm.LastSummary);
        Assert.Empty(_shell.Links);
        Assert.Empty(_dialogs.Informs);

        pages.Throw = new ExtensionPageException("Edge ließ sich nicht mit Ihren normalen Benutzerrechten starten.");
        await vm.RunActionCommand.ExecuteAsync(row);
        var info = Assert.Single(_dialogs.Informs);
        Assert.Equal(("Edge ließ sich nicht öffnen", "Edge ließ sich nicht mit Ihren normalen Benutzerrechten starten."), info);
        pages.Throw = null;

        // without an opener the action is not offered
        var none = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<SecurityItem>>([item]), _engine, _defender, _dialogs, _shell,
            true, _gate);
        await none.RefreshCommand.ExecuteAsync(null);
        Assert.False(none.RunActionCommand.CanExecute(none.Items[0].Children[0]));
    }

    [Fact]
    public async Task Busy_clipboard_still_opens_the_browser_and_shows_the_address()
    {
        var id = new string('a', 32);
        var item = SecurityChecks.Extensions([new BrowserExtension("Edge", "Default", id, "Fremd", ExtensionOrigin.Unknown)], _ => true);
        var pages = new FakePages();
        var vm = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<SecurityItem>>([item]), _engine, _defender, _dialogs, _shell,
            true, _gate, null, pages, _ => throw new System.Runtime.InteropServices.COMException("Zwischenablage belegt"));
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RunActionCommand.ExecuteAsync(vm.Items[0].Children[0]);

        Assert.Equal(["Edge"], pages.Calls);
        Assert.Equal(SecurityViewModel.OpenedNoCopyText("Edge", $"edge://extensions/?id={id}"), vm.LastSummary);
        Assert.Contains($"edge://extensions/?id={id}", vm.LastSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Second_click_while_the_browser_opens_does_nothing()
    {
        var ext = new BrowserExtension("Edge", "Default", new string('a', 32), "Fremd", ExtensionOrigin.Unknown);
        var item = SecurityChecks.Extensions([ext], _ => true);
        using var hold = new ManualResetEventSlim(false);
        var pages = new FakePages { Hold = hold };
        var vm = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<SecurityItem>>([item]), _engine, _defender, _dialogs, _shell,
            true, _gate, null, pages, _ => { });
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Items[0].Children[0];

        var first = vm.RunActionCommand.ExecuteAsync(row);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RunActionCommand.CanExecute(row));
        await vm.RunActionCommand.ExecuteAsync(row);   // ignored while the first one runs
        hold.Set();
        await first;

        Assert.Single(pages.Calls);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RunActionCommand.CanExecute(row));
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

        _dialogs.Answer = false;
        await vm.RunActionCommand.ExecuteAsync(childRow);
        Assert.Empty(_defender.ScannedPaths);

        _dialogs.Answer = true;
        await vm.RunActionCommand.ExecuteAsync(childRow);

        Assert.Equal([path], _defender.ScannedPaths);
        Assert.Contains("geprüft", vm.LastSummary);
    }

    [Fact]
    public async Task Defender_scan_disabled_without_admin_or_while_defender_is_busy()
    {
        var child = new SecurityItem("l1", "tool.exe", Light.Yellow, "Port", "e", SecurityActionKind.DefenderScan, "Prüfen", ScanPath: @"C:\x\tool.exe");
        var items = new[] { Row("listeners", Light.Yellow, children: [child]) };

        var noAdmin = Vm(items, canChange: false);
        await noAdmin.RefreshCommand.ExecuteAsync(null);
        Assert.False(noAdmin.RunActionCommand.CanExecute(noAdmin.Items[0].Children[0]));

        var busy = false;
        var vm = Vm(items, defenderBusy: () => busy);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.RunActionCommand.CanExecute(vm.Items[0].Children[0]));
        busy = true;
        Assert.False(vm.RunActionCommand.CanExecute(vm.Items[0].Children[0]));
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
        var settings = new SettingsViewModel(new AppSettings(), new AppEnvironment(false, _root, new SemVer(0, 3, 0)), update, _shell,
            new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")));
        var performance = FakePerformanceVm.Create(_engine, _dialogs, _shell, _gate);
        return new ShellViewModel(optimize, performance, protect, security, privacy, programs, history, settings, update,
            FakeImproveVm.Idle(_engine, _dialogs, _gate), isElevated: false);
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

        Assert.Equal(new[] { "Übersicht", "Optimieren", "Leistung", "Schutz", "Sicherheit", "Datenschutz", "Programme", "Verlauf" },
            shell.PrimaryNav.Select(n => n.Title));
        var nav = shell.PrimaryNav[4];
        Assert.Equal("", nav.Glyph);
        Assert.Equal("", shell.PrimaryNav[5].Glyph);
        Assert.Same(vm, nav.Page);

        // the first visit reads the check when startup did not
        nav.IsSelected = true;
        Assert.Same(vm, shell.CurrentPage);
        if (vm.RefreshCommand.ExecutionTask is { } t) await t;
        Assert.True(vm.IsLoaded);
    }
}
