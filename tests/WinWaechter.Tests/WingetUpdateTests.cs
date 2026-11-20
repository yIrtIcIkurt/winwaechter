using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Updates;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class WingetUpdateTests
{
    static WingetPackage P(string name, string id, bool user = false) => new(name, id, "1.0", "2.0", false, user);

    static WingetUpdateService Service(FakeWinget winget) =>
        new(winget, new UpdateFilter(RuleSet.Default, () => []), _ => new RestorePointResult(true, "ok"));

    static async Task<(Core.Actions.ActionOutcome Outcome, IReadOnlyList<UpdateResult> Results)> Run(
        WingetUpdateService s, IReadOnlyList<WingetPackage> selected, bool restorePoint = true, CancellationToken ct = default)
    {
        IReadOnlyList<UpdateResult> results = [];
        var a = s.InstallAction(selected, () => Task.FromResult(restorePoint), r => { results = r; return Task.CompletedTask; });
        var outcome = await a.RunAsync(null, ct);
        return (outcome, results);
    }

    [Fact]
    public async Task Duplicate_scope_entries_install_once_and_no_update_found_is_success()
    {
        var winget = new FakeWinget { UpgradeHandler = p => p.Id == "Opera.Opera" ? unchecked((int)0x8A15002B) : 0 };
        var (outcome, results) = await Run(Service(winget), [P("Opera", "Opera.Opera"), P("Opera", "Opera.Opera", user: true), P("7-Zip", "7zip.7zip")]);
        Assert.Equal(["Opera.Opera", "7zip.7zip"], winget.Upgraded.Select(p => p.Id));
        Assert.True(winget.Upgraded[0].UserScope);
        Assert.Equal(UpdateOutcome.AlreadyCurrent, results[0].Outcome);
        Assert.True(outcome.Success);
        Assert.Equal("2 aktualisiert, 0 fehlgeschlagen", outcome.Message);
    }

    [Theory]
    [InlineData(0, UpdateOutcome.Updated)]
    [InlineData(0x8A15002B, UpdateOutcome.AlreadyCurrent)]
    [InlineData(0x8A150109, UpdateOutcome.RestartNeeded)]
    [InlineData(0x8A15010B, UpdateOutcome.RestartNeeded)]
    [InlineData(0x8A15010A, UpdateOutcome.Failed)]
    [InlineData(0x8A150101, UpdateOutcome.Failed)]
    [InlineData(0x8A150008, UpdateOutcome.Failed)]
    [InlineData(0x8A150011, UpdateOutcome.Failed)]
    [InlineData(0x8A150010, UpdateOutcome.Failed)]
    [InlineData(0x8A150105, UpdateOutcome.Failed)]
    [InlineData(0x12345678, UpdateOutcome.Failed)]
    public void Exit_codes_map_to_outcomes(long code, UpdateOutcome expected)
    {
        var (outcome, text) = WingetExitCodes.Map(unchecked((int)code));
        Assert.Equal(expected, outcome);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Exit_code_texts_are_specific()
    {
        Assert.Equal("Das Programm läuft gerade. Schließen Sie es und versuchen Sie es erneut.", WingetExitCodes.Map(unchecked((int)0x8A150101)).Text);
        Assert.Equal("Download fehlgeschlagen. Prüfen Sie die Internetverbindung.", WingetExitCodes.Map(unchecked((int)0x8A150008)).Text);
        Assert.Equal("Fehlgeschlagen (Code 0x12345678), Details im Protokoll.", WingetExitCodes.Map(0x12345678).Text);
    }

    sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task Each_package_reports_running_then_done_failed_or_skipped()
    {
        var winget = new FakeWinget { UpgradeHandler = p => p.Id == "Beispiel.B" ? unchecked((int)0x8A150101) : 0 };
        var items = new SyncProgress<Core.Actions.ItemProgress>();
        var a = Service(winget).InstallAction([P("A", "Beispiel.A"), P("B", "Beispiel.B"), P("Dell Optimizer", "Dell.Optimizer")],
            () => Task.FromResult(true), _ => Task.CompletedTask, items);

        await a.RunAsync(null, CancellationToken.None);

        Assert.Equal(
        [
            ("Beispiel.A", Core.Actions.ItemState.Running), ("Beispiel.A", Core.Actions.ItemState.Done),
            ("Beispiel.B", Core.Actions.ItemState.Running), ("Beispiel.B", Core.Actions.ItemState.Failed),
            ("Dell.Optimizer", Core.Actions.ItemState.Running), ("Dell.Optimizer", Core.Actions.ItemState.Skipped),
        ], items.Items.Select(i => (((WingetPackage)i.Item).Id, i.State)));
        Assert.Equal("Das Programm läuft gerade. Schließen Sie es und versuchen Sie es erneut.",
            items.Items.Single(i => i.State == Core.Actions.ItemState.Failed).Text);
    }

    [Fact]
    public async Task Cancel_stops_after_current_package()
    {
        using var cts = new CancellationTokenSource();
        var winget = new FakeWinget { UpgradeHandler = _ => { cts.Cancel(); return 0; } };
        var (outcome, results) = await Run(Service(winget), [P("A", "Beispiel.A"), P("B", "Beispiel.B")], ct: cts.Token);
        Assert.Single(winget.Upgraded);
        Assert.Single(results);
        Assert.Contains("abgebrochen", outcome.Message);
    }

    [Fact]
    public async Task Protected_package_slipped_into_selection_is_skipped_not_installed()
    {
        var winget = new FakeWinget();
        var (_, results) = await Run(Service(winget), [P("Dell Optimizer", "Dell.Optimizer"), P("7-Zip", "7zip.7zip")]);
        Assert.Equal(["7zip.7zip"], winget.Upgraded.Select(p => p.Id));
        Assert.Equal(UpdateOutcome.Skipped, results[0].Outcome);
        Assert.Equal("Geschützt, ausgelassen.", results[0].Text);
    }

    [Fact]
    public async Task Restore_point_declined_runs_nothing()
    {
        var winget = new FakeWinget();
        var (outcome, _) = await Run(Service(winget), [P("7-Zip", "7zip.7zip")], restorePoint: false);
        Assert.Empty(winget.Upgraded);
        Assert.False(outcome.Success);
        Assert.Equal("Abgebrochen.", outcome.Message);
    }

    [Fact]
    public void Install_action_is_final_skips_gate_and_lists_versions()
    {
        var a = Service(new FakeWinget()).InstallAction([new WingetPackage("7-Zip", "7zip.7zip", "26.00", "26.06", false, false)],
            () => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.True(a.Final);
        Assert.True(a.SkipGate);
        Assert.Equal("updates", a.LockKey);
        Assert.True(a.Stamp);
        Assert.Equal("7-Zip: 26.00 → 26.06", a.Items![0]);
        Assert.StartsWith("1 Programme werden aktualisiert. Ein Update lässt sich nicht über den Verlauf zurücknehmen.", a.ConfirmText);
    }

    [Fact]
    public async Task Load_splits_offered_and_hidden()
    {
        var winget = new FakeWinget { List = new(WingetListStatus.Ok, [P("Dell Optimizer", "Dell.Optimizer"), P("7-Zip", "7zip.7zip")], null) };
        var (list, offered, hidden) = await Service(winget).LoadAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.Ok, list.Status);
        Assert.Equal(["7zip.7zip"], offered.Select(p => p.Id));
        Assert.Equal(["Dell.Optimizer"], hidden.Select(p => p.Id));
    }
}
