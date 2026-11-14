using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Updates;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class UpdatesViewModelTests
{
    readonly RecordingDialogs _dialogs = new();
    readonly FakeWinget _winget = new();
    readonly Dictionary<string, ActivityLock> _locks = ActivityLock.CreateDefaults();

    sealed class LinkShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    readonly LinkShell _shell = new();

    static WingetPackage P(string name, string id, string installed = "1.0", bool unknown = false) =>
        new(name, id, installed, "2.0", unknown, false);

    UpdatesViewModel Vm(bool canChange = true) =>
        new(new WingetUpdateService(_winget, new UpdateFilter(RuleSet.Default, () => []), _ => new RestorePointResult(true, "")),
            new ActionRunner(_dialogs, new ChangeGate(), locks: _locks), _dialogs, _shell, canChange, () => Task.FromResult(true));

    [Fact]
    public async Task Loads_rows_preselects_known_versions_and_counts_hidden()
    {
        _winget.List = new(WingetListStatus.Ok,
            [P("7-Zip", "7zip.7zip"), P("Ubisoft Connect", "Ubisoft.Connect", "< 175", unknown: true), P("Dell Optimizer", "Dell.Optimizer")], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasLoaded);
        Assert.Equal(["7-Zip", "Ubisoft Connect"], vm.Rows.Select(r => r.Name));
        Assert.Equal([true, false], vm.Rows.Select(r => r.IsSelected));
        Assert.Equal("Installierte Version unbekannt", vm.Rows[1].Note);
        Assert.Equal("1.0 → 2.0", vm.Rows[0].Versions);
        Assert.Equal("1 Programme bewusst ausgelassen (geschützt)", vm.HiddenText);
        Assert.Equal("Updates verfügbar (2)", vm.HeaderText);
        Assert.Equal("Ausgewählte aktualisieren (1)", vm.UpdateSelectedText);
    }

    [Fact]
    public async Task No_updates_says_all_current()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Alle Programme sind aktuell", vm.HeaderText);
        Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Missing_winget_shows_store_button_and_no_rows()
    {
        _winget.List = new(WingetListStatus.Missing, [], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowMissing);
        Assert.Empty(vm.Rows);
        vm.OpenStoreCommand.Execute(null);
        Assert.Equal([SystemLinks.AppInstallerStore], _shell.Links);
        Assert.True(SystemLinks.IsAllowed(SystemLinks.AppInstallerStore));
    }

    [Fact]
    public async Task Needs_source_agreement_asks_then_reloads_with_accept_flag()
    {
        _winget.List = new(WingetListStatus.NeedsSourceAgreement, [], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowSourceAgreement);

        _winget.List = new(WingetListStatus.Ok, [P("7-Zip", "7zip.7zip")], null);
        await vm.AcceptSourceAndSearchCommand.ExecuteAsync(null);
        Assert.Equal("Damit nehmen Sie die Bedingungen der winget-Quelle von Microsoft an.", Assert.Single(_dialogs.Confirms).Message);
        Assert.Equal([false, true], _winget.ListCalls);
        Assert.Single(vm.Rows);
        Assert.False(vm.ShowSourceAgreement);
    }

    [Fact]
    public async Task Declined_source_agreement_does_not_search()
    {
        _winget.List = new(WingetListStatus.NeedsSourceAgreement, [], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        _dialogs.ConfirmAnswer = false;
        await vm.AcceptSourceAndSearchCommand.ExecuteAsync(null);
        Assert.Equal([false], _winget.ListCalls);
    }

    [Fact]
    public async Task Update_selected_locked_without_admin_and_while_updates_lock_busy()
    {
        _winget.List = new(WingetListStatus.Ok, [P("7-Zip", "7zip.7zip")], null);
        var locked = Vm(canChange: false);
        await locked.RefreshCommand.ExecuteAsync(null);
        Assert.False(locked.UpdateSelectedCommand.CanExecute(null));

        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
        Assert.True(_locks["updates"].TryBegin());
        Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
        Assert.False(vm.UpdateOneCommand.CanExecute(vm.Rows[0]));
        _locks["updates"].End();
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task After_install_list_is_reloaded_and_result_text_set()
    {
        _winget.List = new(WingetListStatus.Ok, [P("7-Zip", "7zip.7zip"), P("Notepad++", "Notepad++.Notepad++")], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Rows[1].IsSelected = false;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["7zip.7zip"], _winget.Upgraded.Select(p => p.Id));
        Assert.Equal("1 aktualisiert, 0 fehlgeschlagen", vm.ResultText);
        Assert.Equal(2, _winget.ListCalls.Count);
        Assert.True(Assert.Single(_dialogs.Confirms).Final);
    }

    [Fact]
    public async Task Update_one_installs_only_that_row()
    {
        _winget.List = new(WingetListStatus.Ok, [P("7-Zip", "7zip.7zip"), P("Notepad++", "Notepad++.Notepad++")], null);
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.UpdateOneCommand.ExecuteAsync(vm.Rows[1]);
        Assert.Equal(["Notepad++.Notepad++"], _winget.Upgraded.Select(p => p.Id));
    }
}
