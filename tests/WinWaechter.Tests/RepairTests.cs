using WinWaechter.Core.Actions;
using WinWaechter.Core.Repair;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class RepairTests
{
    const string Win = @"C:\Windows";
    const string Local = @"C:\Users\u\AppData\Local";
    static readonly string Sys32 = Path.Combine(Win, "System32");

    readonly FakeProcessRunner _run = new();
    readonly FakeServiceControl _services = new FakeServiceControl().Add("wuauserv").Add("bits", running: false).Add("cryptsvc").Add("Spooler");
    readonly FakeRepairFiles _files = new();
    readonly FakeProcessEnder _ender = new();
    bool _restorePoint = true;
    int _restorePoints;

    RepairActions Actions(params AdapterConfig[] adapters) =>
        new(_run, _services, _files, new FakeNetworkConfig(adapters), () => { _restorePoints++; return Task.FromResult(_restorePoint); },
            _ender, Win, Local, new DateTime(2026, 11, 14), explorerPoll: TimeSpan.FromMilliseconds(5), explorerTimeout: TimeSpan.FromMilliseconds(200));

    IEnumerable<(string Exe, string Args)> Calls => _run.Calls;

    static string Exe(string name) => Path.Combine(Sys32, name);

    [Fact]
    public async Task System_files_runs_dism_restorehealth_then_sfc()
    {
        var a = Actions().SystemFiles();
        Assert.True(a.SkipGate);
        Assert.Equal("repair", a.LockKey);
        Assert.True(a.Stamp);
        Assert.False(a.Final);
        await a.RunAsync(null, CancellationToken.None);
        Assert.Equal([(Exe("dism.exe"), "/Online /Cleanup-Image /RestoreHealth"), (Exe("sfc.exe"), "/scannow")], Calls);
    }

    [Fact]
    public async Task Dism_progress_percent_is_reported()
    {
        _run.Handler = (exe, _) => Task.FromResult((0, exe.EndsWith("dism.exe")
            ? "[==   10.0%   ]\r[====  42.5% ]\r\nDer Vorgang wurde erfolgreich beendet.\r\n"
            : "Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.", ""));
        var reports = new List<string>();
        await Actions().SystemFiles().RunAsync(new SyncProgress(reports), CancellationToken.None);
        Assert.Contains("Systemdateien werden repariert … 42.5 %", reports);
        Assert.DoesNotContain(reports, r => r.EndsWith("42.5 %") && reports.IndexOf(r) < reports.IndexOf("Systemdateien werden repariert … 10.0 %"));
    }

    sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }

    [Theory]
    [InlineData("Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.", "Keine Fehler gefunden.")]
    [InlineData("Windows Resource Protection did not find any integrity violations.", "Keine Fehler gefunden.")]
    [InlineData("Der Windows-Ressourcenschutz hat beschädigte Dateien gefunden und erfolgreich repariert.",
        "Fehler gefunden und repariert. Ein Neustart wird empfohlen.")]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.",
        "Fehler gefunden und repariert. Ein Neustart wird empfohlen.")]
    [InlineData("Der Windows-Ressourcenschutz hat beschädigte Dateien gefunden, konnte aber einige davon nicht reparieren.",
        @"Einige Fehler ließen sich nicht reparieren. Details: C:\Windows\Logs\CBS\CBS.log")]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.",
        @"Einige Fehler ließen sich nicht reparieren. Details: C:\Windows\Logs\CBS\CBS.log")]
    public async Task Sfc_result_texts_for_clean_repaired_and_unfixable_in_german_and_english(string sfc, string expected)
    {
        // sfc writes UTF-16: redirected, every character is followed by a NUL
        var utf16 = string.Concat(sfc.Select(c => c + "\0"));
        _run.Handler = (exe, _) => Task.FromResult((0, exe.EndsWith("sfc.exe") ? utf16 : "ok", ""));
        var r = await Actions().SystemFiles().RunAsync(null, CancellationToken.None);
        Assert.Equal(expected, r.Message);
    }

    [Fact]
    public async Task Dism_failure_is_named_and_sfc_still_runs()
    {
        _run.Handler = (exe, _) => Task.FromResult((exe.EndsWith("dism.exe") ? 87 : 0,
            exe.EndsWith("sfc.exe") ? "Windows Resource Protection did not find any integrity violations." : "Fehler: 87", ""));
        var r = await Actions().SystemFiles().RunAsync(null, CancellationToken.None);
        Assert.Equal(2, _run.Calls.Count);
        Assert.StartsWith("DISM ist fehlgeschlagen (Code 87). sfc wurde trotzdem ausgeführt.", r.Message);
    }

    string Dist => Path.Combine(Win, "SoftwareDistribution");
    string Cat => Path.Combine(Sys32, "catroot2");

    [Fact]
    public async Task Windows_update_repair_stops_renames_and_restarts_running_services()
    {
        _files.Dirs.UnionWith([Dist, Cat]);
        var a = Actions().WindowsUpdate();
        Assert.True(a.Final);
        Assert.Contains("Der Update-Verlauf in den Einstellungen erscheint danach leer; installierte Updates bleiben.", a.ConfirmText);
        var r = await a.RunAsync(null, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal(1, _restorePoints);
        Assert.Contains(Dist + ".bak-20261114", _files.Dirs);
        Assert.Contains(Cat + ".bak-20261114", _files.Dirs);
        var net = Exe("net.exe");
        Assert.Equal(
        [
            (net, "stop wuauserv"), (net, "stop cryptsvc"),
            (net, "start wuauserv"), (net, "start cryptsvc"),
        ], Calls);
    }

    [Fact]
    public async Task Windows_update_repair_restarts_running_services_even_when_rename_fails()
    {
        _files.Dirs.UnionWith([Dist, Cat]);
        _files.MoveThrows = new IOException("Zugriff verweigert");
        var r = await Actions().WindowsUpdate().RunAsync(null, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("Zugriff verweigert", r.Message);
        Assert.Contains((Exe("net.exe"), "start wuauserv"), Calls);
        Assert.Contains((Exe("net.exe"), "start cryptsvc"), Calls);
    }

    [Fact]
    public async Task Windows_update_repair_only_deletes_own_bak_pattern()
    {
        _files.Dirs.UnionWith([Dist, Cat, Dist + ".bak-20261001", Dist + ".old", Cat + ".bak-2026", Cat + ".bak-20260101"]);
        await Actions().WindowsUpdate().RunAsync(null, CancellationToken.None);
        Assert.DoesNotContain(Dist + ".bak-20261001", _files.Dirs);
        Assert.DoesNotContain(Cat + ".bak-20260101", _files.Dirs);
        Assert.Contains(Dist + ".old", _files.Dirs);
        Assert.Contains(Cat + ".bak-2026", _files.Dirs);
    }

    [Fact]
    public async Task Second_repair_on_the_same_day_gets_a_new_name()
    {
        _files.Dirs.UnionWith([Dist, Cat, Dist + ".bak-20261114"]);
        await Actions().WindowsUpdate().RunAsync(null, CancellationToken.None);
        Assert.Contains(Dist + ".bak-20261114-2", _files.Dirs);
    }

    [Fact]
    public async Task Declined_restore_point_stops_windows_update_repair()
    {
        _restorePoint = false;
        _files.Dirs.UnionWith([Dist, Cat]);
        var r = await Actions().WindowsUpdate().RunAsync(null, CancellationToken.None);
        Assert.Equal("Abgebrochen.", r.Message);
        Assert.Empty(Calls);
    }

    [Fact]
    public async Task Dns_and_renew_use_ipconfig()
    {
        var acts = Actions();
        Assert.Null(acts.FlushDns().ConfirmText);
        Assert.Equal("Die Verbindung ist kurz weg.", acts.RenewIp().ConfirmText);
        await acts.FlushDns().RunAsync(null, CancellationToken.None);
        await acts.RenewIp().RunAsync(null, CancellationToken.None);
        var ip = Exe("ipconfig.exe");
        Assert.Equal([(ip, "/flushdns"), (ip, "/release"), (ip, "/renew")], Calls);
    }

    [Fact]
    public void Network_reset_confirmation_lists_static_ip_and_dns()
    {
        var a = Actions(new AdapterConfig("WLAN", false, ["192.168.1.20"], true, ["9.9.9.9", "149.112.112.112"]),
            new AdapterConfig("Ethernet", true, ["10.0.0.5"], false, []),
            new AdapterConfig("Bluetooth", false, [], false, [])).NetworkReset();
        Assert.True(a.Final);
        Assert.Contains("Von Hand eingetragene IP-Adressen und DNS-Server werden gelöscht.", a.ConfirmText);
        Assert.Equal(["WLAN: DNS 9.9.9.9, 149.112.112.112", "Ethernet: IP 10.0.0.5"], a.Items);
    }

    // review: disconnected adapters (dock, Bluetooth) are not IPEnabled in WMI but still lose their fixed settings
    [Fact]
    public void Network_config_from_registry_covers_disconnected_adapters()
    {
        const string v4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        const string v6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";
        const string names = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
        var reg = new FakeRegistry()
            .Seed(Hive.LocalMachine, v4 + @"\{aaa}", "EnableDHCP", 0, Microsoft.Win32.RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, v4 + @"\{aaa}", "IPAddress", new[] { "192.168.178.5" }, Microsoft.Win32.RegistryValueKind.MultiString)
            .Seed(Hive.LocalMachine, v4 + @"\{aaa}", "NameServer", "9.9.9.9,149.112.112.112")
            .Seed(Hive.LocalMachine, names + @"\{aaa}\Connection", "Name", "Ethernet 2 (Dock)")
            .Seed(Hive.LocalMachine, v4 + @"\{bbb}", "EnableDHCP", 1, Microsoft.Win32.RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, v4 + @"\{bbb}", "NameServer", "")
            .Seed(Hive.LocalMachine, v6 + @"\{bbb}", "NameServer", "2620:fe::fe")
            .Seed(Hive.LocalMachine, names + @"\{bbb}\Connection", "Name", "WLAN")
            .Seed(Hive.LocalMachine, v4 + @"\{ccc}", "EnableDHCP", 1, Microsoft.Win32.RegistryValueKind.DWord);
        var list = new RegistryNetworkConfig(reg).Read();
        var dock = list.Single(a => a.Name == "Ethernet 2 (Dock)");
        Assert.True(dock.StaticIp);
        Assert.Equal(["192.168.178.5"], dock.Ips);
        Assert.Equal(["9.9.9.9", "149.112.112.112"], dock.Dns);
        var wlan = list.Single(a => a.Name == "WLAN");
        Assert.False(wlan.StaticIp);
        Assert.Equal(["2620:fe::fe"], wlan.Dns);
        Assert.False(list.Single(a => a.Name == "{ccc}").StaticDns);
    }

    // review: every repair shares one lock (spec: one repair at a time)
    [Fact]
    public void Every_repair_uses_the_repair_lock()
    {
        _files.FileSet.Add(Path.Combine(Spool, "1.SPL"));
        var a = Actions();
        foreach (var act in new[] { a.SystemFiles(), a.WindowsUpdate(), a.FlushDns(), a.RenewIp(), a.NetworkReset(), a.PrintQueue()!, a.IconCache(),
                     a.ScanDisk('C'), a.FixDiskOnRestart('C') })
            Assert.Equal("repair", act.LockKey);
    }

    [Fact]
    public async Task Network_reset_runs_winsock_and_ip_reset_after_restore_point()
    {
        var r = await Actions().NetworkReset().RunAsync(null, CancellationToken.None);
        Assert.Equal(1, _restorePoints);
        var netsh = Exe("netsh.exe");
        Assert.Equal([(netsh, "winsock reset"), (netsh, "int ip reset")], Calls);
        Assert.Equal("Netzwerk zurückgesetzt. Bitte starten Sie den Rechner neu.", r.Message);
    }

    string Spool => Path.Combine(Sys32, "spool", "PRINTERS");

    [Fact]
    public void Print_queue_empty_builds_no_action()
    {
        var acts = Actions();
        Assert.True(acts.PrintQueueEmpty);
        Assert.Null(acts.PrintQueue());
    }

    [Fact]
    public async Task Print_queue_stops_spooler_deletes_files_and_starts_it()
    {
        _files.FileSet.UnionWith([Path.Combine(Spool, "00001.SPL"), Path.Combine(Spool, "00001.SHD")]);
        var a = Actions().PrintQueue()!;
        Assert.True(a.Final);
        Assert.Equal("Wartende Druckaufträge werden gelöscht.", a.ConfirmText);
        await a.RunAsync(null, CancellationToken.None);
        Assert.Empty(_files.FileSet);
        Assert.Equal([(Exe("net.exe"), "stop Spooler"), (Exe("net.exe"), "start Spooler")], Calls);
    }

    [Fact]
    public async Task Icon_cache_counts_locked_files()
    {
        var explorer = new RunningProcess("explorer.exe", Path.Combine(Win, "explorer.exe"), 10);
        _ender.Running.Add(explorer);
        var explorerDir = Path.Combine(Local, "Microsoft", "Windows", "Explorer");
        _files.FileSet.UnionWith([Path.Combine(Local, "IconCache.db"), Path.Combine(explorerDir, "iconcache_32.db"),
            Path.Combine(explorerDir, "thumbcache_256.db"), Path.Combine(explorerDir, "other.db")]);
        _files.Locked.Add(Path.Combine(explorerDir, "thumbcache_256.db"));
        // Windows brings the shell back by itself shortly after
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            lock (_ender.Running) _ender.Running.Add(explorer with { Pid = 11 });
        });
        var r = await Actions().IconCache().RunAsync(null, CancellationToken.None);
        Assert.Equal([explorer], _ender.Killed);
        Assert.Equal([Path.Combine(explorerDir, "other.db"), Path.Combine(explorerDir, "thumbcache_256.db")], _files.FileSet.Order());
        Assert.Contains("1 Dateien waren gesperrt und bleiben bis zum nächsten Neustart.", r.Message);
    }

    [Fact]
    public async Task Chkdsk_scan_error_output_sets_ScanFoundErrors()
    {
        var acts = Actions();
        var scan = acts.ScanDisk('C');
        Assert.Null(scan.ConfirmText);
        Assert.True(scan.SkipGate);
        Assert.Equal("repair", scan.LockKey);
        _run.Handler = (_, _) => Task.FromResult((0, "Windows hat das Dateisystem überprüft und keine Probleme gefunden.", ""));
        await scan.RunAsync(null, CancellationToken.None);
        Assert.False(acts.ScanFoundErrors);
        _run.Handler = (_, _) => Task.FromResult((3, "Windows hat Probleme im Dateisystem gefunden.", ""));
        var r = await scan.RunAsync(null, CancellationToken.None);
        Assert.True(acts.ScanFoundErrors);
        Assert.False(r.Success);
        Assert.Equal((Exe("chkdsk.exe"), "C: /scan"), _run.Calls[0]);
    }

    [Fact]
    public async Task Fix_on_restart_is_final_and_uses_fsutil_dirty_set()
    {
        var a = Actions().FixDiskOnRestart('D');
        Assert.True(a.Final);
        Assert.Contains("Der nächste Start dauert länger; schalten Sie den Rechner dabei nicht aus.", a.ConfirmText);
        var r = await a.RunAsync(null, CancellationToken.None);
        Assert.Equal([(Exe("fsutil.exe"), "dirty set D:")], Calls);
        Assert.Equal("Die Reparatur läuft beim nächsten Neustart.", r.Message);
    }

    [Theory]
    [InlineData("Es wurde keine Komponentenspeicherbeschädigung erkannt.", false)]
    [InlineData("No component store corruption detected.", false)]
    [InlineData("Der Komponentenspeicher kann repariert werden.", true)]
    [InlineData("The component store is repairable.", true)]
    [InlineData("Fehler: 740", null)]
    public async Task CheckHealth_parses_three_states(string output, bool? expected)
    {
        _run.Handler = (_, _) => Task.FromResult((0, output, ""));
        Assert.Equal(expected, await new RepairChecks(_run, Win).ComponentStoreRepairableAsync(CancellationToken.None));
        Assert.Equal((Exe("dism.exe"), "/Online /Cleanup-Image /CheckHealth"), _run.Calls[0]);
    }

    [Theory]
    [InlineData("Volume - C: ist fehlerhaft.", true)]
    [InlineData("Volume - C: ist NICHT fehlerhaft.", false)]
    [InlineData("Volume - C: is Dirty", true)]
    [InlineData("Volume - C: is NOT Dirty", false)]
    [InlineData("Zugriff verweigert", null)]
    public async Task Dirty_query_parses(string output, bool? expected)
    {
        _run.Handler = (_, _) => Task.FromResult((0, output, ""));
        Assert.Equal(expected, await new RepairChecks(_run, Win).IsDirtyAsync('C', CancellationToken.None));
    }

    [Theory]
    [InlineData('1')]
    [InlineData('c')]
    [InlineData(':')]
    public void Drive_letter_must_be_A_to_Z(char drive)
    {
        Assert.Throws<ArgumentException>(() => Actions().ScanDisk(drive));
        Assert.Throws<ArgumentException>(() => Actions().FixDiskOnRestart(drive));
    }
}
