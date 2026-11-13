using Microsoft.Win32;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class BrowserBackgroundTests : IDisposable
{
    const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
    const string ChromeKey = @"SOFTWARE\Policies\Google\Chrome";
    const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-browserbg-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakePolicyOwnership _own = new();

    public BrowserBackgroundTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    PerformanceScanner Scanner(bool withPolicies = true) => new(_reg, new FakePowerModes(), new FakeAnimationSetting(),
        new FakeHibernation(), new FakeAppDisplayNames(), true, new FakeBroadcast(), new FakePowerPlans(),
        withPolicies ? () => _own : null);

    void Installed(string exe) => _reg.SeedKey(Hive.LocalMachine, AppPaths + exe);

    IReadOnlyList<PerfSetting> Browser(PerformanceScanner? s = null) =>
        (s ?? Scanner()).Scan().Where(r => r.Group == "browser-background").ToList();

    static PerfSetting Row(IReadOnlyList<PerfSetting> rows, string id) => rows.Single(r => r.Id == id);

    [Fact]
    public void Rows_only_for_installed_browsers()
    {
        Assert.Empty(Browser());

        Installed("msedge.exe");
        var edge = Browser();
        Assert.Equal(["edge-startupboost", "edge-background"], edge.Select(r => r.Id));
        Assert.Equal("Edge: Start-Boost", edge[0].Title);
        Assert.Equal("Edge: Erweiterungen und Apps im Hintergrund, wenn Edge geschlossen ist", edge[1].Title);

        Installed("chrome.exe");
        var chrome = Row(Browser(), "chrome-background");
        Assert.Equal("Chrome: Apps im Hintergrund weiter ausführen", chrome.Title);
        Assert.Equal(1, chrome.Current);
        Assert.True(chrome.Available);
        Assert.Equal(0, chrome.Recommended);
        Assert.Equal([1, 0], chrome.Options.Select(o => o.Value));
        Assert.Equal(["An", "Aus"], chrome.Options.Select(o => o.Label));
        Assert.Equal("Spart Arbeitsspeicher und Akku; der Browser startet nur Sekundenbruchteile langsamer.", chrome.Explanation);
    }

    [Fact]
    public void Foreign_policy_is_shown_not_changed()
    {
        Installed("msedge.exe");
        _reg.Seed(Hive.LocalMachine, EdgeKey, "StartupBoostEnabled", 0, RegistryValueKind.DWord);

        var rows = Browser();
        var boost = Row(rows, "edge-startupboost");
        Assert.False(boost.Available);
        Assert.Equal(0, boost.Current);
        Assert.Equal("Von einer Richtlinie festgelegt", boost.UnavailableText);
        Assert.Empty(Scanner().ChangesFor(boost, 1));

        // the other Edge value has no policy: still offered
        Assert.True(Row(rows, "edge-background").Available);

        // without a journal every present policy value counts as foreign
        _reg.Seed(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled", 0, RegistryValueKind.DWord);
        _own.Own(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled");
        var unknown = Browser(Scanner(withPolicies: false));
        Assert.Equal("Von einer Richtlinie festgelegt", Row(unknown, "edge-background").UnavailableText);
    }

    sealed class ThrowingOwnership : IPolicyOwnership
    {
        public bool IsOurs(Hive h, string key, string name) => throw new IOException("Journal kaputt");
    }

    [Fact]
    public void Failing_policy_source_falls_back_to_foreign_and_keeps_rows()
    {
        Installed("msedge.exe");
        _reg.Seed(Hive.LocalMachine, EdgeKey, "StartupBoostEnabled", 0, RegistryValueKind.DWord);
        IReadOnlyList<PerfSetting> Scan(Func<IPolicyOwnership> f) => new PerformanceScanner(_reg, new FakePowerModes(),
            new FakeAnimationSetting(), new FakeHibernation(), new FakeAppDisplayNames(), true, new FakeBroadcast(),
            new FakePowerPlans(), f).Scan().Where(r => r.Group == "browser-background").ToList();

        foreach (var rows in new[] { Scan(() => throw new IOException("kaputt")), Scan(() => new ThrowingOwnership()) })
        {
            Assert.Equal(2, rows.Count);
            Assert.Equal("Von einer Richtlinie festgelegt", Row(rows, "edge-startupboost").UnavailableText);
            Assert.True(Row(rows, "edge-background").Available);
        }
    }

    [Fact]
    public void Own_policy_off_points_to_history()
    {
        Installed("chrome.exe");
        _reg.Seed(Hive.LocalMachine, ChromeKey, "BackgroundModeEnabled", 0, RegistryValueKind.DWord);
        _own.Own(Hive.LocalMachine, ChromeKey, "BackgroundModeEnabled");

        var row = Row(Browser(), "chrome-background");
        Assert.False(row.Available);
        Assert.Equal(0, row.Current);
        Assert.Equal("Aus – wieder einschalten über den Verlauf", row.UnavailableText);
        Assert.Empty(Scanner().ChangesFor(row, 1));
    }

    [Fact]
    public void Apply_creates_policy_and_undo_deletes_it()
    {
        Installed("msedge.exe");
        var scanner = Scanner();
        var row = Row(Browser(scanner), "edge-background");
        var change = Assert.Single(scanner.ChangesFor(row, 0));
        Assert.Equal("perfvalue", change.Kind);
        Assert.False(_reg.KeyExists(Hive.LocalMachine, EdgeKey));

        var rec = change.Apply(Ctx());
        Assert.Equal(0, _reg.Get(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled"));
        Assert.Equal(RegistryValueKind.DWord, _reg.GetKind(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled"));
        Assert.Equal("false", rec.Before["existed"]);

        // rehydrated from the journal, undo removes the value again
        var restored = PerfValueChange.FromRecord(rec, _reg, new FakeBroadcast());
        restored.Undo(rec, Ctx());
        Assert.Null(_reg.GetKind(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled"));

        // after the undo the browser is back to its default
        Assert.Equal(1, Row(Browser(scanner), "edge-background").Current);
    }

    [Fact]
    public void Managed_hint_only_when_browser_had_no_policy()
    {
        Installed("msedge.exe");
        Installed("chrome.exe");
        var rows = Browser();
        const string edgeHint = "Edge zeigt danach „Von Ihrer Organisation verwaltet“; das ist normal und verschwindet beim Rückgängigmachen.";
        Assert.Equal(edgeHint, Row(rows, "edge-startupboost").Hint);
        Assert.Equal(edgeHint, Row(rows, "edge-background").Hint);
        Assert.Equal("Chrome zeigt danach „Von Ihrer Organisation verwaltet“; das ist normal und verschwindet beim Rückgängigmachen.",
            Row(rows, "chrome-background").Hint);
        Assert.Equal(edgeHint, PerformanceScanner.ManagedHint("Edge"));

        // Edge already has a foreign policy, Chrome only an empty key: only Chrome keeps the hint
        _reg.Seed(Hive.LocalMachine, EdgeKey, "HideFirstRunExperience", 1, RegistryValueKind.DWord);
        _reg.SeedKey(Hive.LocalMachine, ChromeKey);
        rows = Browser();
        Assert.Null(Row(rows, "edge-background").Hint);
        Assert.NotNull(Row(rows, "chrome-background").Hint);

        // a subkey counts as a policy, too
        _reg.SeedKey(Hive.LocalMachine, ChromeKey + @"\ExtensionSettings");
        Assert.Null(Row(Browser(), "chrome-background").Hint);
    }

    [Fact]
    public void FromRecord_rejects_target_1_for_browser_values()
    {
        foreach (var v in new[] { PerfValues.EdgeStartupBoost, PerfValues.EdgeBackgroundMode, PerfValues.ChromeBackgroundMode })
        {
            Assert.Equal([0], v.Targets);
            Assert.Equal(1, v.DefaultWhenMissing);
            Assert.Same(v, PerfValues.Find(v.Hive, v.Key, v.Name));
            Assert.Throws<ArgumentException>(() => new PerfValueChange(_reg, v, 1, "x", null));

            var record = new ChangeRecord("1", "perfvalue", "t", "x",
                new() { ["hive"] = "HKLM", ["key"] = v.Key, ["name"] = v.Name },
                new() { ["value"] = "1" }, ChangeStatus.Applied, DateTimeOffset.Now);
            Assert.Throws<InvalidOperationException>(() => PerfValueChange.FromRecord(record, _reg, new FakeBroadcast()));

            var ok = record with { After = new() { ["value"] = "0" } };
            Assert.NotNull(PerfValueChange.FromRecord(ok, _reg, new FakeBroadcast()));
        }
    }

    sealed record Page(PerformanceViewModel Vm, RecordingDialogs Dialogs, List<Finding> Findings, Func<int> Opened);

    Page NewPage(FakeRegistry reg)
    {
        var store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(reg, new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), RuleSet.Default);
        PerformanceRegistration.Register(factory, reg, new FakePowerModes(), new FakeAnimationSetting(), new FakeHibernation(),
            new FakeBroadcast(), new FakePowerPlans());
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"));
        var scanner = new PerformanceScanner(reg, new FakePowerModes(), new FakeAnimationSetting(), new FakeHibernation(),
            new FakeAppDisplayNames(), true, new FakeBroadcast(), new FakePowerPlans(), () => _own);
        var dialogs = new RecordingDialogs();
        var gate = new ChangeGate();
        var findings = new List<Finding>();
        var tools = new PerformanceTools(() => findings.ToList(), RuleSet.Default, new FakeProcessEnder(),
            () => new ProcessGuardContext(@"C:\Windows", 4242, @"C:\Program Files\WinWaechter\WinWaechter.App.exe",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), _ => false),
            new ActionRunner(dialogs, gate), () => null);
        var opened = 0;
        var vm = new PerformanceViewModel(
            _ => Task.FromResult<(IReadOnlyList<PerfSetting>, Func<PerfSetting, int, IReadOnlyList<IChange>>)>(
                (scanner.Scan(), scanner.ChangesFor)),
            new SystemSampler(new FakeSystemSources(), (_, _) => { }), () => new BootReport([], null, [], null),
            engine, dialogs, new NullShell(), true, gate, () => opened++, new FakeTicks(), null, tools);
        return new Page(vm, dialogs, findings, () => opened);
    }

    [Fact]
    public async Task Background_launchers_hint_names_active_autostarts()
    {
        var reg = new FakeRegistry();
        var (vm, _, findings, opened) = NewPage(reg);

        Finding Auto(string title, bool withChange = true) => new($"autostart|{title}", FindingArea.Autostart, title,
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\" + title, [], Rating.Info, false,
            withChange ? new StartupApprovedChange(reg, Hive.CurrentUser, "Run", title) : null);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Null(vm.BackgroundLaunchersHint);

        // no change (not changeable) and unrelated names: no hint
        findings.AddRange([Auto("Steam", withChange: false), Auto("Dropbox")]);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Null(vm.BackgroundLaunchersHint);

        // word boundary: „Eagle Browser“ is no launcher, „EA app“ is; one name uses the singular
        findings.Add(Auto("Eagle Browser"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Null(vm.BackgroundLaunchersHint);
        findings.Add(Auto("EA app"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("EA app startet mit Windows und läuft im Hintergrund. Das regeln Sie unter Optimieren → Autostart.",
            vm.BackgroundLaunchersHint);

        findings.AddRange([Auto("Steam"), Auto("Epic Games Launcher")]);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("EA app, Steam und Epic Games Launcher starten mit Windows und laufen im Hintergrund. Das regeln Sie unter Optimieren → Autostart.",
            vm.BackgroundLaunchersHint);
        vm.OpenOptimizeCommand.Execute(null);
        Assert.Equal(1, opened());
    }

    [Fact]
    public async Task Confirm_line_carries_the_managed_hint()
    {
        var reg = new FakeRegistry();
        reg.SeedKey(Hive.LocalMachine, AppPaths + "msedge.exe");
        var (vm, dialogs, _, _) = NewPage(reg);
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = vm.BrowserBackground.Single(r => r.Setting.Id == "edge-background");
        row.Chosen = 0;
        await vm.ApplyCommand.ExecuteAsync(null);

        var line = Assert.Single(dialogs.Confirms.Single().Items);
        Assert.Equal("Edge: Hintergrundmodus ausschalten – " + PerformanceScanner.ManagedHint("Edge"), line);
        Assert.Equal(0, reg.Get(Hive.LocalMachine, EdgeKey, "BackgroundModeEnabled"));
    }

    sealed class NullShell : ISystemShell
    {
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) { }
    }
}
