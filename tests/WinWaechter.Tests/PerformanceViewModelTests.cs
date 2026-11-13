using Microsoft.Win32;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PerformanceViewModelTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-perfvm-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakePowerModes _power = new();
    readonly FakeAnimationSetting _anim = new();
    readonly FakeHibernation _hib = new();
    readonly FakeAppDisplayNames _names = new();
    readonly FakeBroadcast _broadcast = new();
    readonly FakeSystemSources _src = new();
    readonly FakeBootEventSource _boot = new();
    readonly FakeTicks _ticks = new();
    readonly ChangeGate _gate = new();
    readonly RecordingDialogs _dialogs = new();
    readonly RecordingShell _shell = new();
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;
    readonly PerformanceScanner _scanner;
    int _optimizeOpened;

    public PerformanceViewModelTests()
    {
        _store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(_reg, new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), RuleSet.Default);
        PerformanceRegistration.Register(factory, _reg, _power, _anim, _hib, _broadcast, new FakePowerPlans());
        _engine = new OptimizerEngine([], _store, factory, _ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt."));
        _scanner = new PerformanceScanner(_reg, _power, _anim, _hib, _names, true, _broadcast);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class RecordingDialogs : IDialogService
    {
        public List<IReadOnlyList<string>> ConfirmItems { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Items)> Informs { get; } = [];

        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            ConfirmItems.Add(items ?? []);
            return true;
        }

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) =>
            Informs.Add((title, message, items ?? []));
    }

    sealed class RecordingShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    sealed class BlockingSources(ManualResetEventSlim gate, FakeSystemSources inner) : ISystemSources
    {
        public int Cores => inner.Cores;
        public CpuTimes? Cpu() { gate.Wait(); return inner.Cpu(); }
        public (long Used, long Total)? Memory() => inner.Memory();
        public double? DiskBusy() => inner.DiskBusy();
        public IReadOnlyList<ProcTime> Processes() => inner.Processes();
    }

    Task<(IReadOnlyList<PerfSetting> Settings, Func<PerfSetting, int, IReadOnlyList<IChange>> ChangesFor)> Scan(CancellationToken _) =>
        Task.FromResult<(IReadOnlyList<PerfSetting>, Func<PerfSetting, int, IReadOnlyList<IChange>>)>(
            (_scanner.Scan(), _scanner.ChangesFor));

    PerformanceViewModel Vm(bool canChange = true, ISystemSources? sources = null, Func<BootReport>? boot = null) => new(
        Scan,
        new SystemSampler(sources ?? _src, (_, _) => { }),
        boot ?? (() => BootReport.Load(_boot, DateTime.UtcNow)),
        _engine, _dialogs, _shell, canChange, _gate, () => _optimizeOpened++, _ticks, _scanner.FastStartupActive);

    static PerfSettingViewModel Row(PerformanceViewModel vm, string id) =>
        vm.Settings.Concat(vm.BackgroundApps).Single(r => r.Setting.Id == id);

    static async Task Tick(PerformanceViewModel vm, FakeTicks ticks)
    {
        ticks.Fire();
        if (vm.LastSampling is { } t) await t;
    }

    [Fact]
    public async Task Nothing_pending_after_refresh()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Settings);
        Assert.All(vm.Settings, r => Assert.False(r.IsChanged));
        Assert.Equal(0, vm.PendingCount);
        Assert.Equal("Änderungen anwenden (0)", vm.ApplyText);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("An", Row(vm, "hibernate").StateText);
    }

    [Fact]
    public async Task Choosing_values_counts_pending_and_apply_journals_changes()
    {
        var raised = false;
        var vm = Vm();
        vm.ChangesApplied += (_, _) => raised = true;
        await vm.RefreshCommand.ExecuteAsync(null);

        Row(vm, "transparency").Chosen = 0;
        Row(vm, "power-ac").Chosen = 2;
        Assert.Equal(2, vm.PendingCount);
        Assert.Equal("Änderungen anwenden (2)", vm.ApplyText);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(2, Assert.Single(_dialogs.ConfirmItems).Count);
        var session = Assert.Single(_store.List());
        Assert.Equal("Leistung", session.Title);
        Assert.Equal(2, session.Records.Count);
        Assert.All(session.Records, r => Assert.Equal(ChangeStatus.Applied, r.Status));
        var t = PerfValues.Transparency;
        Assert.Equal(0, _reg.Get(t.Hive, t.Key, t.Name));
        Assert.Equal([(PowerSource.Ac, PowerModeIds.BestPerformance)], _power.SetCalls);
        Assert.True(raised);
        Assert.Equal(0, vm.PendingCount);
        Assert.Equal(0, Row(vm, "transparency").Setting.Current);
        Assert.Equal("Zuletzt: 2 angewendet.", vm.LastSummary);

        var undo = _engine.UndoSession(session);
        Assert.Equal(2, undo.Undone);
        Assert.Equal(PowerModeIds.Balanced, _power.Get(PowerSource.Ac));
    }

    [Fact]
    public async Task Reset_clears_pending()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Row(vm, "gamemode").Chosen = 0;
        Assert.Equal(1, vm.PendingCount);

        vm.ResetCommand.Execute(null);

        Assert.Equal(0, vm.PendingCount);
        Assert.False(Row(vm, "gamemode").IsChanged);
    }

    [Fact]
    public async Task Apply_disabled_without_admin()
    {
        var vm = Vm(canChange: false);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(Row(vm, "transparency").CanChange);
        Row(vm, "transparency").Chosen = 0;
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.ShowAdminNotice);
        Assert.False(string.IsNullOrEmpty(vm.AdminNotice));
    }

    [Fact]
    public async Task Unavailable_row_cannot_change()
    {
        var t = PerfValues.Transparency;
        _reg.Seed(t.Hive, t.Key, t.Name, "1");
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(Row(vm, "transparency").CanChange);
        Row(vm, "transparency").Chosen = 0;
        Assert.Equal(0, vm.PendingCount);
    }

    [Fact]
    public async Task Background_note_is_not_an_app_row()
    {
        _reg.ThrowingKeys.Add(PerfValues.BackgroundRoot);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.BackgroundApps);
        Assert.DoesNotContain(vm.Settings, r => r.Setting.Group == "background");
        Assert.True(vm.ShowBackgroundNote);
        Assert.Equal("Nicht lesbar", vm.BackgroundNote);
    }

    [Fact]
    public async Task Background_apps_are_listed_separately()
    {
        _reg.SeedKey(Hive.CurrentUser, PerfValues.BackgroundRoot + "\\Microsoft.Foo_8wekyb3d8bbwe");
        _names.Names["Microsoft.Foo_8wekyb3d8bbwe"] = "Foo";
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        var app = Assert.Single(vm.BackgroundApps);
        Assert.Equal("Foo", app.Setting.Title);
        Assert.DoesNotContain(vm.Settings, r => r.Setting.Group == "background");
        Assert.False(vm.ShowBackgroundNote);
    }

    [Fact]
    public async Task Monitor_runs_only_while_visible()
    {
        var vm = Vm();
        _src.CpuValue = new CpuTimes(0, 0, 0);
        vm.OnShown();
        Assert.True(_ticks.Running);
        Assert.Equal(TimeSpan.FromSeconds(2), _ticks.Every);
        // showing the page takes one priming sample at once
        await vm.LastSampling!;
        Assert.Equal(1, _src.CpuCalls);
        Assert.Equal("–", vm.CpuText);

        _src.CpuValue = new CpuTimes(50, 100, 0);
        await Tick(vm, _ticks);
        Assert.Equal(2, _src.CpuCalls);
        Assert.Equal("50 %", vm.CpuText);

        // hidden: even a tick that still arrives (queued before Stop) samples nothing
        vm.OnHidden();
        Assert.False(_ticks.Running);
        var last = vm.LastSampling;
        _ticks.FireEvenIfStopped();
        Assert.Same(last, vm.LastSampling);
        Assert.Equal(2, _src.CpuCalls);

        // shown again after a long pause: the baseline is reset, no average over the hidden time
        _src.CpuValue = new CpuTimes(1050, 2100, 0);
        vm.OnShown();
        await vm.LastSampling!;
        Assert.Equal(3, _src.CpuCalls);
        Assert.Equal("–", vm.CpuText);
        _src.CpuValue = new CpuTimes(1060, 2200, 0);
        await Tick(vm, _ticks);
        Assert.Equal(4, _src.CpuCalls);
        Assert.Equal("90 %", vm.CpuText);
    }

    [Fact]
    public async Task Overlapping_ticks_are_skipped()
    {
        var gate = new ManualResetEventSlim();
        var vm = Vm(sources: new BlockingSources(gate, _src));
        vm.OnShown(); // the priming sample blocks
        var first = vm.LastSampling!;
        _ticks.Fire();
        _ticks.Fire();
        Assert.Same(first, vm.LastSampling);
        gate.Set();
        await first;
        Assert.Equal(1, _src.CpuCalls);
    }

    [Fact]
    public async Task Readouts_format_and_null_shows_dash()
    {
        var vm = Vm();
        Assert.Equal("–", vm.CpuText);
        Assert.Equal("–", vm.MemoryText);
        Assert.Equal("–", vm.DiskText);

        _src.CpuValue = new CpuTimes(0, 0, 0);
        _src.MemoryValue = ((long)(12.34 * (1L << 30)), (long)(31.6 * (1L << 30)));
        _src.DiskValue = 12.4;
        _src.Procs =
        [
            new ProcTime(1, "chrome", null, 100L << 20),
            new ProcTime(2, "chrome", null, 50L << 20),
        ];
        vm.OnShown();
        await vm.LastSampling!;
        // the priming sample has nothing to compare with; memory is a snapshot and shows at once
        Assert.Equal("–", vm.CpuText);
        Assert.Equal("12,3 von 31,6 GB", vm.MemoryText);
        Assert.Equal("–", vm.DiskText);
        Assert.Equal(0.0, vm.CpuValue);

        _src.CpuValue = new CpuTimes(63, 100, 0);
        await Tick(vm, _ticks);
        Assert.Equal("37 %", vm.CpuText);
        Assert.Equal(0.37, vm.CpuValue, 3);
        Assert.Equal("12 %", vm.DiskText);
        Assert.Equal(0.124, vm.DiskValue, 3);

        var top = Assert.Single(vm.Top);
        Assert.Equal("chrome (2)", top.Name);
        Assert.Equal("–", top.CpuText);
        Assert.Equal(WinWaechter.Core.Model.SizeText.Format(150L << 20), top.MemoryText);

        _src.MemoryValue = null;
        _src.DiskValue = null;
        await Tick(vm, _ticks);
        Assert.Equal("–", vm.MemoryText);
        Assert.Equal("–", vm.DiskText);
        Assert.Equal(0.0, vm.MemoryValue);
    }

    [Fact]
    public async Task Restart_note_after_gpu_change()
    {
        var g = PerfValues.GpuScheduling;
        _reg.Seed(g.Hive, g.Key, g.Name, 2, RegistryValueKind.DWord);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        var gpu = Row(vm, "gpu");
        Assert.True(gpu.NeedsRestart);
        gpu.Chosen = 1;

        await vm.ApplyCommand.ExecuteAsync(null);

        var summary = Assert.Single(_dialogs.Informs, i => i.Title == "Zusammenfassung");
        Assert.Contains("Einige Änderungen gelten erst nach einem Neustart.", summary.Message);
    }

    [Fact]
    public async Task No_restart_note_for_other_changes()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Row(vm, "gamemode").Chosen = 0;

        await vm.ApplyCommand.ExecuteAsync(null);

        var summary = Assert.Single(_dialogs.Informs, i => i.Title == "Zusammenfassung");
        Assert.DoesNotContain("Neustart", summary.Message);
    }

    [Fact]
    public async Task Boot_message_shown()
    {
        _boot.Throws = new UnauthorizedAccessException();
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(BootReport.AccessDenied, vm.BootMessage);
        Assert.Equal("–", vm.BootText);
        Assert.Equal("", vm.BootAverageText);
        Assert.Empty(vm.BootSlowest);
    }

    [Fact]
    public async Task Boot_times_are_formatted_and_source_read_once_per_refresh()
    {
        var calls = 0;
        _boot.Events.Add(new BootEvent(100, DateTime.UtcNow.AddHours(-1), null, 42_400, null));
        _boot.Events.Add(new BootEvent(100, DateTime.UtcNow.AddHours(-30), null, 48_000, null));
        _boot.Events.Add(new BootEvent(101, DateTime.UtcNow.AddHours(-1), "Foo.exe", null, 7_600));
        _boot.Events.Add(new BootEvent(102, DateTime.UtcNow.AddHours(-1), "Bar.sys", null, 12_600));
        var vm = Vm(boot: () => { calls++; return BootReport.Load(_boot, DateTime.UtcNow); });

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
        Assert.Equal("42 s", vm.BootText);
        Assert.Equal("Durchschnitt: 45 s", vm.BootAverageText);
        // below 10 s with one decimal, otherwise whole seconds
        Assert.Equal(["Bar.sys: 13 s", "Foo.exe: 7,6 s"], vm.BootSlowest);
        Assert.Null(vm.BootMessage);
    }

    [Fact]
    public async Task Short_boot_times_show_one_decimal()
    {
        _boot.Events.Add(new BootEvent(100, DateTime.UtcNow.AddHours(-1), null, 2_400, null));
        _boot.Events.Add(new BootEvent(100, DateTime.UtcNow.AddHours(-2), null, 9_960, null));
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("2,4 s", vm.BootText);
        Assert.Equal("Durchschnitt: 6,2 s", vm.BootAverageText);
    }

    [Fact]
    public async Task Fast_startup_note_only_when_on()
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowFastStartupNote); // HiberbootEnabled missing

        _reg.Seed(Hive.LocalMachine, key, "HiberbootEnabled", 1, RegistryValueKind.DWord);
        _hib.IsEnabled = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowFastStartupNote);

        _hib.IsEnabled = false; // no hibernation, no fast startup
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowFastStartupNote);

        _hib.IsEnabled = true;
        _reg.Seed(Hive.LocalMachine, key, "HiberbootEnabled", 0, RegistryValueKind.DWord);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowFastStartupNote);
    }

    [Fact]
    public async Task Unreadable_value_while_applying_is_shown_in_the_summary()
    {
        const string app = "Contoso.App_abc123";
        var disabled = PerfValues.BackgroundDisabled(app);
        _reg.Seed(disabled.Hive, disabled.Key, disabled.Name, 0, RegistryValueKind.DWord);
        var byUser = PerfValues.BackgroundDisabledByUser(app);
        _reg.Seed(byUser.Hive, byUser.Key, byUser.Name, "1"); // REG_SZ: unexpected type
        _names.Names[app] = "Beispiel";
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Row(vm, "bg:" + app).Chosen = 0;

        await vm.ApplyCommand.ExecuteAsync(null);

        var summary = Assert.Single(_dialogs.Informs);
        Assert.Equal("Zusammenfassung", summary.Title);
        Assert.Contains("DisabledByUser", summary.Message);
        Assert.Equal(summary.Message, vm.LastSummary);
        Assert.Empty(_dialogs.ConfirmItems);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task Open_link_and_open_optimize_commands()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Row(vm, "transparency").OpenLinkCommand.Execute(null);
        Assert.Equal(["ms-settings:easeofaccess-visualeffects"], _shell.Links);
        Assert.False(Row(vm, "gamemode").OpenLinkCommand.CanExecute(null));

        vm.OpenOptimizeCommand.Execute(null);
        Assert.Equal(1, _optimizeOpened);
    }

    [Fact]
    public async Task Apply_is_blocked_while_another_page_holds_the_gate()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Row(vm, "gamemode").Chosen = 0;
        var release = new TaskCompletionSource();
        var other = _gate.RunAsync(new object(), () => release.Task);

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal(ChangeGate.BusyHint, vm.GateHint);
        release.SetResult();
        await other;
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    sealed class NoUpdates : IUpdateService
    {
        public Task<WinWaechter.Core.Update.UpdateCheckResult> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<WinWaechter.Core.Update.VerifiedUpdate> DownloadAndVerifyAsync(WinWaechter.Core.Update.UpdateInfo info,
            IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        public void Launch(WinWaechter.Core.Update.VerifiedUpdate update) { }
    }

    ShellViewModel Shell(PerformanceViewModel performance)
    {
        var optimize = new OptimizeViewModel(_engine, _dialogs, canChange: false, _gate);
        var protect = new ProtectViewModel(new FakeDefender(), (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]),
            _engine, _ => 0, _dialogs, canChange: false, _gate);
        var security = new SecurityViewModel(_ => Task.FromResult<IReadOnlyList<WinWaechter.Core.Security.Checks.SecurityItem>>([]),
            _engine, new FakeDefender(), _dialogs, _shell, canChange: false, _gate);
        var privacy = new PrivacyViewModel(
            _ => Task.FromResult(new WinWaechter.Core.Privacy.PrivacyScanner(new FakeRegistry(), RuleSet.Default).Scan(_ => true)),
            _engine, _dialogs, canChange: false, _gate);
        var programs = new ProgramsViewModel(new FakeProgramActions(), _engine, _dialogs, _shell, canChange: false, _gate);
        var history = new HistoryViewModel(_store, _engine, new WinWaechter.Core.Optimizer.Storage.AblageJanitor(_store), _dialogs,
            canChange: false, _gate);
        var update = new UpdateViewModel(new NoUpdates(), _dialogs, canInstall: false, () => { }, _gate);
        var settings = new SettingsViewModel(new AppSettings(), new AppEnvironment(false, _root, new WinWaechter.Core.Update.SemVer(0, 4, 0)),
            update, _shell, new RulesInfo(1, false, null),
            _ => Task.FromResult(new WinWaechter.Core.Update.RulesUpdateResult(null, "")));
        return new ShellViewModel(optimize, performance, protect, security, privacy, programs, history, settings, update, isElevated: false);
    }

    [Fact]
    public async Task Leistung_nav_starts_and_stops_monitor()
    {
        var scans = 0;
        var vm = new PerformanceViewModel(ct => { Interlocked.Increment(ref scans); return Scan(ct); },
            new SystemSampler(_src, (_, _) => { }), () => BootReport.Load(_boot, DateTime.UtcNow),
            _engine, _dialogs, _shell, true, _gate, () => _optimizeOpened++, _ticks);
        var shell = Shell(vm);

        Assert.Equal(new[] { "Übersicht", "Optimieren", "Leistung", "Schutz", "Sicherheit", "Datenschutz", "Programme", "Verlauf" },
            shell.PrimaryNav.Select(n => n.Title));
        var nav = shell.PrimaryNav[2];
        Assert.Equal("", nav.Glyph);
        Assert.Same(vm, nav.Page);
        Assert.Same(vm, shell.Performance);
        Assert.False(_ticks.Running);

        // the first visit reads the settings and starts the monitor
        nav.IsSelected = true;
        Assert.Same(vm, shell.CurrentPage);
        Assert.True(_ticks.Running);
        if (vm.RefreshCommand.ExecutionTask is { } t) await t;
        Assert.NotEmpty(vm.Settings);
        Assert.Equal(1, scans);

        // leaving the page stops it
        shell.PrimaryNav[0].IsSelected = true;
        Assert.False(_ticks.Running);

        // a second visit restarts the monitor without reading again
        nav.IsSelected = true;
        Assert.True(_ticks.Running);
        Assert.Equal(1, scans);

        shell.SettingsNav.IsSelected = true;
        Assert.False(_ticks.Running);
    }
}
