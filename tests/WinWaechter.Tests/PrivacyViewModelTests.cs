using Microsoft.Win32;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PrivacyViewModelTests : IDisposable
{
    const string EffKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-privvm-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly RuleSet _rules = RuleSet.Default;
    readonly ChangeGate _gate = new();
    readonly RecordingDialogs _dialogs = new();
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;

    public PrivacyViewModelTests()
    {
        _store = new SessionStore(_root);
        var factory = ChangeFactory.CreateDefault(_reg, new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), _rules);
        _engine = new OptimizerEngine([], _store, factory, _ => new RestorePointResult(true, "Wiederherstellungspunkt erstellt."));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class RecordingDialogs : IDialogService
    {
        public List<IReadOnlyList<string>> ConfirmItems { get; } = [];
        public List<string> InformTitles { get; } = [];

        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
        {
            ConfirmItems.Add(items ?? []);
            return true;
        }

        public void Inform(string title, string message, IReadOnlyList<string>? items = null) => InformTitles.Add(title);
    }

    PrivacyViewModel Vm(bool canChange = true) =>
        new(_ => Task.FromResult(new PrivacyScanner(_reg, _rules).Scan(_ => true)), _engine, _dialogs, canChange, _gate);

    PrivacySetting Setting(string id) => _rules.Privacy.Single(s => s.Id == id);

    static PrivacyRowViewModel Row(PrivacyViewModel vm, string id) => vm.Rows.Single(r => r.Row.Setting.Id == id);

    [Fact]
    public async Task Nothing_preselected_after_refresh()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Rows);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Rows, r => Assert.False(r.IsSelected));
        Assert.Equal("Ausgewählte anwenden (0)", vm.ApplyText);
    }

    [Fact]
    public async Task Select_recommended_skips_not_recommended_and_off_rows()
    {
        foreach (var v in Setting("tipps").Values) _reg.Seed(v.Hive, v.Key, v.Name, v.Target, RegistryValueKind.DWord);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Aus", Row(vm, "tipps").StateText);

        vm.SelectRecommendedCommand.Execute(null);

        Assert.False(Row(vm, "programmstarts").IsSelected);
        Assert.False(Row(vm, "tipps").IsSelected);
        var others = vm.Rows.Where(r => r.CanSelect && r.Row.Setting.Id is not ("programmstarts" or "tipps")).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, r => Assert.True(r.IsSelected));
        Assert.Equal(others.Count, vm.SelectedCount);
        Assert.Equal($"Ausgewählte anwenden ({others.Count})", vm.ApplyText);
    }

    async Task<(PrivacyViewModel Vm, SessionJournal Session)> ApplyVorschlaege(bool[] changesApplied)
    {
        var values = Setting("vorschlaege").Values;
        // two values exist with the "on" value, two are missing
        _reg.Seed(values[0].Hive, values[0].Key, values[0].Name, 1, RegistryValueKind.DWord);
        _reg.Seed(values[1].Hive, values[1].Key, values[1].Name, 1, RegistryValueKind.DWord);

        var vm = Vm();
        vm.ChangesApplied += (_, _) => changesApplied[0] = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        Row(vm, "vorschlaege").IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        await vm.ApplyCommand.ExecuteAsync(null);
        return (vm, _store.List()[0]);
    }

    [Fact]
    public async Task Apply_writes_one_change_per_value_and_journals_them()
    {
        var raised = new bool[1];
        var (vm, session) = await ApplyVorschlaege(raised);

        Assert.Equal(4, Assert.Single(_dialogs.ConfirmItems).Count);
        Assert.Equal(4, _reg.SetCalls.Count);
        Assert.Equal("Datenschutz", session.Title);
        Assert.Equal(4, session.Records.Count);
        Assert.All(session.Records, r =>
        {
            Assert.Equal("regvalue", r.Kind);
            Assert.Equal(ChangeStatus.Applied, r.Status);
        });
        Assert.Equal("Aus", Row(vm, "vorschlaege").StateText);
        Assert.False(Row(vm, "vorschlaege").CanSelect);
        Assert.True(raised[0]);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Undo_session_restores_privacy_values()
    {
        var (_, session) = await ApplyVorschlaege(new bool[1]);
        var values = Setting("vorschlaege").Values;

        var result = _engine.UndoSession(session);

        Assert.Equal(4, result.Undone);
        Assert.Equal(1, _reg.Get(values[0].Hive, values[0].Key, values[0].Name));
        Assert.Equal(1, _reg.Get(values[1].Hive, values[1].Key, values[1].Name));
        Assert.Null(_reg.Get(values[2].Hive, values[2].Key, values[2].Name));
        Assert.Null(_reg.Get(values[3].Hive, values[3].Key, values[3].Name));
    }

    [Fact]
    public async Task Already_minimal_row_cannot_be_selected()
    {
        _reg.Seed(Hive.LocalMachine, EffKey, "AllowTelemetry", 0, RegistryValueKind.DWord);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Row(vm, "diagnosedaten");
        Assert.Equal("Aus (bereits minimal)", row.StateText);
        Assert.Equal(Tone.Good, row.StateTone);
        Assert.False(row.CanSelect);
        row.IsSelected = true;
        Assert.False(row.IsSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Apply_disabled_without_admin()
    {
        var vm = Vm(canChange: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectRecommendedCommand.Execute(null);

        Assert.True(vm.SelectedCount > 0);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.ShowAdminNotice);
    }

    sealed class NoUpdates : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Launch(VerifiedUpdate update) { }
    }

    sealed class NoShell : ISystemShell
    {
        public void OpenFolder(string path) { }
    }

    [Fact]
    public async Task Shell_nav_has_datenschutz_between_schutz_and_verlauf()
    {
        var optimize = new OptimizeViewModel(_engine, _dialogs, canChange: false, _gate);
        var protect = new ProtectViewModel(new FakeDefender(), (_, _) => Task.FromResult<IReadOnlyList<Finding>>([]),
            _engine, _ => 0, _dialogs, canChange: false, _gate);
        var privacy = Vm(canChange: false);
        var history = new HistoryViewModel(_store, _engine, new AblageJanitor(_store), _dialogs, canChange: false, _gate);
        var update = new UpdateViewModel(new NoUpdates(), _dialogs, canInstall: false, () => { }, _gate);
        var settings = new SettingsViewModel(new AppSettings(), new AppEnvironment(false, _root, new SemVer(0, 2, 0)), update, new NoShell());
        var shell = new ShellViewModel(optimize, protect, privacy, history, settings, update, isElevated: false);

        Assert.Equal(new[] { "Übersicht", "Optimieren", "Schutz", "Datenschutz", "Verlauf" }, shell.PrimaryNav.Select(n => n.Title));
        var nav = shell.PrimaryNav[3];
        Assert.Equal("", nav.Glyph);
        Assert.Same(privacy, nav.Page);

        // first visit reads the settings
        nav.IsSelected = true;
        Assert.Same(privacy, shell.CurrentPage);
        if (privacy.RefreshCommand.ExecutionTask is { } t) await t;
        Assert.NotEmpty(privacy.Rows);
    }
}
