using Microsoft.Win32;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class AutostartTests : IDisposable
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-autostart-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeFileOps _files = new() { ReportAllPresent = true };
    readonly FakeSignatures _sig = new();
    readonly FakeShortcutResolver _lnk = new();
    readonly FakeUserContext _user;

    public AutostartTests()
    {
        Directory.CreateDirectory(_root);
        _user = new FakeUserContext(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    AutostartScanner Scanner() => new(_reg, _user, _sig, RuleSet.Default, _files, _lnk);
    SessionContext Ctx() => new(Path.Combine(_root, "session"));

    static byte[] Bytes(byte b0) { var x = new byte[12]; x[0] = b0; return x; }

    StartupApprovedChange Change(string name = "Steam") =>
        new(_reg, Hive.CurrentUser, ApprovedRun, name);

    [Fact]
    public void Steam_in_HKCU_run_is_preselected_entbehrlich()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Steam", "\"C:\\Games\\Steam\\steam.exe\" -silent");
        _sig.Signed(@"C:\Games\Steam\steam.exe", "Valve");

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.NotNull(f.Change);
        Assert.Equal("Steam", f.Title);
        Assert.Equal("Valve", f.Publisher);
        Assert.Equal("autostart|HKCU|Run|Steam", f.Id);
        Assert.Contains(f.Reasons, r => r.Contains("Valve"));
    }

    [Fact]
    public void Dell_entry_is_not_listed()
    {
        _reg.Seed(Hive.LocalMachine, Run, "DellUpdate", @"C:\Program Files\Dell\Update\du.exe");
        _sig.Signed(@"C:\Program Files\Dell\Update\du.exe", "Dell Inc");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Already_disabled_entry_has_no_change()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Steam", @"C:\Games\Steam\steam.exe");
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Steam", Bytes(3), RegistryValueKind.Binary);

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Null(f.Change);
    }

    [Fact]
    public void Unknown_entry_is_info_and_not_preselected()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Modeller", @"C:\Apps\foo.exe");
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.NotNull(f.Change);
    }

    [Fact]
    public void Unclear_status_is_not_preselected()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Steam", @"C:\Games\Steam\steam.exe");
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Steam", Bytes(9), RegistryValueKind.Binary);
        var f = Assert.Single(Scanner().Scan());
        Assert.False(f.Preselected);
        Assert.Contains("Status unklar", f.Reasons);
    }

    [Fact]
    public void Wow64_run_uses_Run32_approved_key()
    {
        _reg.Seed(Hive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Steam", @"C:\Games\Steam\steam.exe");
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("autostart|HKLM|Run32|Steam", f.Id);
        Assert.NotNull(f.Change);

        _reg.Seed(Hive.LocalMachine, ApprovedRoot + "Run32", "Steam", Bytes(3), RegistryValueKind.Binary);
        Assert.Null(Assert.Single(Scanner().Scan()).Change);
    }

    [Fact]
    public void Folder_items_map_to_their_hive_and_approved_subkey()
    {
        Directory.CreateDirectory(_user.StartupDir);
        Directory.CreateDirectory(_user.CommonStartupDir);
        var mine = Path.Combine(_user.StartupDir, "Steam.lnk");
        var common = Path.Combine(_user.CommonStartupDir, "Discord.lnk");
        File.WriteAllText(mine, "x");
        File.WriteAllText(common, "x");
        _lnk.Set(mine, @"C:\Games\Steam\steam.exe").Set(common, @"C:\Apps\Discord.exe");
        // Disable the common one in HKLM only; the user one must stay enabled.
        _reg.Seed(Hive.LocalMachine, ApprovedRoot + "StartupFolder", "Discord.lnk", Bytes(3), RegistryValueKind.Binary);

        var found = Scanner().Scan().ToDictionary(f => f.Id);
        Assert.Equal(2, found.Count);
        var mineFinding = found["autostart|HKCU|StartupFolder|Steam.lnk"];
        Assert.NotNull(mineFinding.Change);
        Assert.Equal(@"HKCU\" + ApprovedRoot + @"StartupFolder\Steam.lnk", mineFinding.Change!.Target);
        Assert.Null(found["autostart|HKLM|StartupFolder|Discord.lnk"].Change);
    }

    [Fact]
    public void Startup_folder_lnk_is_resolved_and_classified()
    {
        Directory.CreateDirectory(_user.StartupDir);
        var lnk = Path.Combine(_user.StartupDir, "Discord.lnk");
        File.WriteAllText(lnk, "x");
        File.WriteAllText(Path.Combine(_user.StartupDir, "desktop.ini"), "x");
        Directory.CreateDirectory(Path.Combine(_user.StartupDir, "sub"));
        File.WriteAllText(Path.Combine(_user.StartupDir, "sub", "Steam.lnk"), "x");
        _lnk.Set(lnk, @"C:\Users\x\Discord\Discord.exe");

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("Discord", f.Title);
        Assert.Equal(lnk, f.Location);
        Assert.Equal("autostart|HKCU|StartupFolder|Discord.lnk", f.Id);
        Assert.Equal(Rating.Entbehrlich, f.Rating);
    }

    [Fact]
    public void Startup_folder_file_with_future_timestamp_is_listed()
    {
        Directory.CreateDirectory(_user.StartupDir);
        var exe = Path.Combine(_user.StartupDir, "Sneaky.exe");
        File.WriteAllText(exe, "x");
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddYears(5));

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("Sneaky", f.Title);
        Assert.Equal(exe, f.Location);
    }

    [Fact]
    public void Missing_startup_folders_yield_nothing() => Assert.Empty(Scanner().Scan());

    [Fact]
    public void Apply_writes_03_and_records_before()
    {
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Other", Bytes(2), RegistryValueKind.Binary); // key exists, Steam absent
        var rec = Change().Apply(Ctx());

        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Null(rec.Before["value"]);
        var live = Assert.IsType<byte[]>(_reg.Get(Hive.CurrentUser, ApprovedRun, "Steam"));
        Assert.Equal(12, live.Length);
        Assert.Equal(3, live[0]);
        Assert.Equal(Convert.ToHexString(live), rec.After["value"]);
        Assert.Single(_reg.ExportCalls);
        Assert.Equal("autostart", rec.Kind);
        Assert.Contains("Steam", rec.Description);
    }

    [Fact]
    public void Apply_works_when_approved_key_missing()
    {
        var change = new StartupApprovedChange(_reg, Hive.LocalMachine, ApprovedRoot + "Run32", "Steam");
        var rec = change.Apply(Ctx());

        Assert.Empty(_reg.ExportCalls);
        Assert.Null(rec.Before["backup"]);
        Assert.Equal(3, Assert.IsType<byte[]>(_reg.Get(Hive.LocalMachine, ApprovedRoot + "Run32", "Steam"))[0]);
    }

    [Fact]
    public void Apply_exports_backup_when_key_exists()
    {
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Other", Bytes(2), RegistryValueKind.Binary);
        var rec = Change().Apply(Ctx());
        Assert.NotNull(rec.Before["backup"]);
        Assert.True(File.Exists(rec.Before["backup"]));
    }

    [Fact]
    public void Undo_restores_previous_bytes()
    {
        var prev = Bytes(2);
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Steam", prev, RegistryValueKind.Binary);
        var change = Change();
        var rec = change.Apply(Ctx());

        change.Undo(rec, Ctx());

        Assert.Equal(prev, Assert.IsType<byte[]>(_reg.Get(Hive.CurrentUser, ApprovedRun, "Steam")));
        Assert.Equal(RegistryValueKind.Binary, _reg.KindOf(Hive.CurrentUser, ApprovedRun, "Steam"));
    }

    [Fact]
    public void Undo_deletes_value_if_absent_before()
    {
        var change = Change();
        var rec = change.Apply(Ctx());
        change.Undo(rec, Ctx());
        Assert.Null(_reg.Get(Hive.CurrentUser, ApprovedRun, "Steam"));
    }

    [Fact]
    public void Drift_detected_when_reenabled()
    {
        var change = Change();
        var rec = change.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Steam", Bytes(2), RegistryValueKind.Binary);
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));
    }

    [Fact]
    public void Drift_detected_when_value_removed()
    {
        var change = Change();
        var rec = change.Apply(Ctx());
        _reg.Delete(Hive.CurrentUser, ApprovedRun, "Steam");
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));
    }

    [Fact]
    public void FromRecord_rebuilds_a_working_change()
    {
        _reg.Seed(Hive.CurrentUser, ApprovedFolder, "A.lnk", Bytes(2), RegistryValueKind.Binary);
        var change = new StartupApprovedChange(_reg, Hive.CurrentUser, ApprovedFolder, "A.lnk");
        var rec = change.Apply(Ctx());

        var again = StartupApprovedChange.FromRecord(rec, _reg);
        again.Undo(rec, Ctx());

        Assert.Equal(Bytes(2), Assert.IsType<byte[]>(_reg.Get(Hive.CurrentUser, ApprovedFolder, "A.lnk")));
        Assert.Equal(DriftState.Changed, again.CheckDrift(rec));
    }
}
