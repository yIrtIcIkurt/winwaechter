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

    RepairViewModel Vm(bool canChange = true, bool ownAccount = true) =>
        new(Actions(), new RepairChecks(_run, @"C:\Windows"), new ActionRunner(_dialogs, new ChangeGate(), locks: _locks, stamps: Stamps),
            Stamps, canChange, 'C', ownAccount);

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
    public async Task Running_repair_disables_other_long_rows()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var sfc = vm.Rows.Single(r => r.Id == "repair-sfc");
        Assert.True(sfc.RunCommand.CanExecute(null));
        Assert.True(_locks["repair"].TryBegin());
        Assert.False(sfc.RunCommand.CanExecute(null));
        Assert.Equal("Eine Reparatur läuft gerade.", sfc.LockText);
        // a short tool keeps working
        Assert.True(vm.Rows.Single(r => r.Id == "repair-dns").RunCommand.CanExecute(null));
        _locks["repair"].End();
        Assert.True(sfc.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Run_records_result_and_last_run()
    {
        var vm = Vm();
        await vm.LoadCommand.ExecuteAsync(null);
        var dns = vm.Rows.Single(r => r.Id == "repair-dns");
        await dns.RunCommand.ExecuteAsync(null);
        Assert.Equal("Der DNS-Cache wurde geleert.", dns.ResultText);
        Assert.StartsWith("Zuletzt: ", dns.LastRun);
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
