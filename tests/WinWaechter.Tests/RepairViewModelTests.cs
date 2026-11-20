using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Repair;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class RepairViewModelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ww-repairvm").FullName;
    readonly FakeProcessRunner _run = new();
    readonly RecordingDialogs _dialogs = new();
    readonly Dictionary<string, ActivityLock> _locks = ActivityLock.CreateDefaults();
    ActionStampStore Stamps => new(Path.Combine(_dir, "aktionen.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    RepairActions Actions() => new(_run, new FakeServiceControl(), new FakeRepairFiles(), new FakeNetworkConfig(),
        () => Task.FromResult(true), new FakeProcessEnder(), @"C:\Windows", @"C:\Users\u\AppData\Local", new DateTime(2026, 11, 14));

    RepairViewModel Vm(bool canChange = true, bool ownAccount = true, ISystemShell? shell = null) =>
        new(Actions(), new RepairChecks(_run, @"C:\Windows"), new ActionRunner(_dialogs, new ChangeGate(), locks: _locks, stamps: Stamps),
            Stamps, canChange, 'C', ownAccount, shell);

    [Fact]
    public async Task Rows_show_last_run_from_stamps_or_never()
    {
        Stamps.Set(new ActionStamp("repair-dns", new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.FromHours(1)), true, "Der DNS-Cache wurde geleert."));
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(8, vm.Rows.Count);
        Assert.Equal(["Systemdateien prüfen und reparieren", "Windows Update reparieren", "DNS-Cache leeren", "Verbindung neu aufbauen",
            "Netzwerk komplett zurücksetzen", "Druckerwarteschlange leeren", "Symbole und Miniaturansichten neu aufbauen", "Datenträger prüfen"],
            vm.Rows.Select(r => r.Title));
        Assert.Equal("Zuletzt: 10.11.2026 08:30 – Der DNS-Cache wurde geleert.", vm.Rows.Single(r => r.Id == "repair-dns").LastRun);
        Assert.Equal("Noch nie ausgeführt", vm.Rows.Single(r => r.Id == "repair-sfc").LastRun);
        Assert.Equal("Wenn Webseiten nicht laden, andere aber schon.", vm.Rows.Single(r => r.Id == "repair-dns").When);
    }

    [Fact]
    public async Task Repairable_store_moves_sfc_to_top_as_recommended()
    {
        _run.Handler = (exe, _) => Task.FromResult((0, exe.EndsWith("dism.exe") ? "Der Komponentenspeicher kann repariert werden." : "Volume - C: ist fehlerhaft.", ""));
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["repair-sfc", "repair-chkdsk-scan"], vm.Rows.Take(2).Select(r => r.Id));
        Assert.True(vm.Rows[0].IsRecommended);
        Assert.True(vm.Rows[1].IsRecommended);
        Assert.False(vm.Rows[2].IsRecommended);
    }

    [Fact]
    public async Task Running_repair_disables_every_other_repair()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        Assert.True(sfc.RunCommand.CanExecute(null));
        Assert.True(_locks["repair"].TryBegin());
        Assert.False(sfc.RunCommand.CanExecute(null));
        Assert.Equal("Eine Reparatur läuft gerade.", sfc.LockText);
        // spec 4.3: one repair at a time – the short tools wait too (Windows Update must not be reset during DISM)
        Assert.All(vm.Rows, r => Assert.False(r.RunCommand.CanExecute(null), r.Id));
        Assert.False(vm.LoadCommand.CanExecute(null));
        _locks["repair"].End();
        Assert.True(sfc.RunCommand.CanExecute(null));
        Assert.True(vm.LoadCommand.CanExecute(null));
    }

    // review: „Abbrechen“ of the system-file repair takes effect before sfc
    [Fact]
    public async Task Cancel_before_sfc_stops_after_dism()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        Assert.True(sfc.CanCancel);
        Assert.False(vm.Rows.Single(r => r.Id == "repair-dns").CanCancel);
        _run.Handler = (exe, _) =>
        {
            if (exe.EndsWith("dism.exe")) sfc.CancelCommand.Execute(null);
            return Task.FromResult((0, "", ""));
        };
        _run.Calls.Clear();
        await sfc.RunCommand.ExecuteAsync(null);
        Assert.Single(_run.Calls);
        Assert.EndsWith("– Abgebrochen.", sfc.LastRun);
        Assert.Null(sfc.ResultText);
    }

    // checklist 0.6.0: the result stood twice under the row („Zuletzt: … – X“ and X again)
    [Fact]
    public async Task Run_shows_its_result_once_in_the_last_run_line()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var dns = vm.Rows.Single(r => r.Id == "repair-dns");
        await dns.RunCommand.ExecuteAsync(null);
        Assert.StartsWith("Zuletzt: ", dns.LastRun);
        Assert.EndsWith("– Der DNS-Cache wurde geleert.", dns.LastRun);
        Assert.Null(dns.ResultText);
    }

    const int RepairContentMissing = unchecked((int)0x800F0915);

    // checklist 0.6.0: „DISM ist fehlgeschlagen (Code -2146498283)“ said nothing; now plain text and the way out
    [Fact]
    public async Task Dism_without_repair_files_says_so_and_offers_reinstall_through_windows()
    {
        var shell = new LinkShell();
        var vm = Vm(shell: shell);
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        _run.Handler = (exe, _) => Task.FromResult(exe.EndsWith("dism.exe")
            ? (RepairContentMissing, "", "")
            : (0, "Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.", ""));

        await sfc.RunCommand.ExecuteAsync(null);

        Assert.Contains("Ersatzdateien", sfc.LastRun);
        Assert.Contains("0x800F0915", sfc.LastRun);
        Assert.DoesNotContain("-2146498283", sfc.LastRun);
        Assert.Contains("Keine Fehler gefunden.", sfc.LastRun);
        Assert.True(sfc.ShowLink);
        await sfc.LinkCommand.ExecuteAsync(null);
        Assert.Equal([Core.Sys.SystemLinks.Recovery], shell.Links);
    }

    [Fact]
    public async Task System_files_are_not_recommended_again_right_after_a_run_without_repair_files()
    {
        _run.Handler = (exe, _) => Task.FromResult((0, exe.EndsWith("dism.exe") ? "Der Komponentenspeicher kann repariert werden." : "", ""));
        Stamps.Set(new ActionStamp("repair-sfc", DateTimeOffset.Now.AddDays(-1), false, "Windows konnte die Ersatzdateien nicht laden.",
            Core.Sys.SystemLinks.Recovery));
        var vm = Vm(shell: new LinkShell());
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        Assert.False(sfc.IsRecommended);
        Assert.True(sfc.ShowLink);   // the way out stays under the row

        // a month later Windows Update may have the files: recommended again
        Stamps.Set(new ActionStamp("repair-sfc", DateTimeOffset.Now.AddDays(-31), false, "x", Core.Sys.SystemLinks.Recovery));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.Rows.Single(r => r.Id == "repair-sfc").IsRecommended);
    }

    // device test 2026-11-20: a run from before the fix kept its old text and no link – it must count the same
    [Fact]
    public async Task An_older_run_without_repair_files_counts_the_same_and_shows_plain_text()
    {
        _run.Handler = (exe, _) => Task.FromResult((0, exe.EndsWith("dism.exe") ? "Der Komponentenspeicher kann repariert werden." : "", ""));
        Stamps.Set(new ActionStamp("repair-sfc", DateTimeOffset.Now.AddDays(-5), false,
            "DISM ist fehlgeschlagen (Code -2146498283). sfc wurde trotzdem ausgeführt. Keine Fehler gefunden."));
        var vm = Vm(shell: new LinkShell());
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        Assert.False(sfc.IsRecommended);
        Assert.True(sfc.ShowLink);
        Assert.Contains("Ersatzdateien", sfc.LastRun);
        Assert.Contains("0x800F0915", sfc.LastRun);
        Assert.DoesNotContain("-2146498283", sfc.LastRun);
    }

    sealed class LinkShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    [Fact]
    public async Task Scan_errors_reveal_fix_on_restart_button()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var disk = vm.Rows.Single(r => r.Id == "repair-chkdsk-scan");
        Assert.False(disk.ShowFix);
        _run.Handler = (_, _) => Task.FromResult((3, "Windows hat Probleme im Dateisystem gefunden.", ""));
        await disk.RunCommand.ExecuteAsync(null);
        Assert.True(disk.ShowFix);
        await disk.FixCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(_dialogs.Confirms).Final);
        Assert.Contains(_run.Calls, c => c.Args == "dirty set C:");
    }

    [Fact]
    public async Task Empty_print_queue_says_so_without_running()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var spool = vm.Rows.Single(r => r.Id == "repair-spool");
        await spool.RunCommand.ExecuteAsync(null);
        Assert.Equal("Die Warteschlange ist leer.", spool.ResultText);
        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public async Task Locked_without_admin_and_icons_need_own_account()
    {
        var locked = Vm(canChange: false);
        await locked.LoadCommand.ExecuteAsync(null);
        Assert.All(locked.Rows, r => Assert.False(r.RunCommand.CanExecute(null)));

        var other = Vm(ownAccount: false);
        await other.LoadCommand.ExecuteAsync(null);
        var icons = other.Rows.Single(r => r.Id == "repair-icons");
        Assert.False(icons.RunCommand.CanExecute(null));
        Assert.Equal("Nur für das angemeldete Benutzerkonto möglich.", icons.LockText);
    }
}
