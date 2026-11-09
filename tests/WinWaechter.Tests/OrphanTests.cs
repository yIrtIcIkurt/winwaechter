using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class OrphanTests : IDisposable
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-orphan-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeSignatures _sig = new();
    readonly FakeFileOps _files = new();
    readonly FakeServiceControl _sc = new();
    readonly FakeTaskStore _store = new();
    readonly FakeUserContext _user;

    public OrphanTests()
    {
        Directory.CreateDirectory(_root);
        _user = new FakeUserContext(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string Gone(string name) => Path.Combine(_root, "gone", name);

    IReadOnlyList<Finding> Autostarts() =>
        new AutostartScanner(_reg, _user, _sig, RuleSet.Default, _files, new FakeShortcutResolver()).Scan();

    [Fact]
    public void Autostart_with_missing_exe_is_orphan_and_preselected()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Foo", $"\"{Gone("foo.exe")}\" --min");

        var f = Assert.Single(Autostarts());
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.Equal(FindingGroups.OrphanEntries, f.Group);
        Assert.Equal([OrphanRules.OrphanReason], f.Reasons);
        Assert.NotNull(f.Change);
        Assert.Empty(_sig.VerifyCalls);
    }

    [Fact]
    public void Disabled_orphan_autostart_offers_no_change()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Foo", $"\"{Gone("foo.exe")}\"");
        _reg.Seed(Hive.CurrentUser, ApprovedRun, "Foo", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

        var f = Assert.Single(Autostarts());
        Assert.Null(f.Change);
        Assert.False(f.Preselected);
        Assert.Equal(FindingGroups.OrphanEntries, f.Group);
    }

    [Fact]
    public void Protected_orphan_stays_hidden()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Dell Update", $"\"{Gone("du.exe")}\"");
        Assert.Empty(Autostarts());
    }

    [Fact]
    public void Unc_path_is_not_orphan()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Nas", @"\\nas\x\a.exe");

        var f = Assert.Single(Autostarts());
        Assert.Null(f.Group);
        Assert.DoesNotContain(OrphanRules.OrphanReason, f.Reasons);
    }

    [Fact]
    public void Unready_drive_is_not_orphan()
    {
        _files.NotReady.Add(Path.GetPathRoot(_root)!);
        _reg.Seed(Hive.CurrentUser, Run, "Gone", $"\"{Gone("g.exe")}\"");

        var f = Assert.Single(Autostarts());
        Assert.Null(f.Group);
        Assert.DoesNotContain(OrphanRules.OrphanReason, f.Reasons);
    }

    [Fact]
    public void Service_with_missing_image_is_orphan()
    {
        _sc.Add("GoneSvc", imagePath: $"\"{Gone("svc.exe")}\" -k");

        var f = Assert.Single(new ServiceScanner(_sc, _sig, RuleSet.Default, _reg, _files).Scan());
        Assert.Equal(FindingGroups.OrphanEntries, f.Group);
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.Equal([OrphanRules.OrphanReason], f.Reasons);
        Assert.IsType<ServiceStartModeChange>(f.Change);
        Assert.Empty(_sig.VerifyCalls);
    }

    [Fact]
    public void Task_with_missing_exe_is_orphan_but_com_handler_task_is_not()
    {
        _store.Add(@"\GoneTask", actionExe: Gone("t.exe"));
        _store.Add(@"\ComTask", hidden: true, actionExe: null);

        var all = new TaskScanner(_store, _sig, RuleSet.Default, _files, _user).Scan();
        var a = Assert.Single(all, f => f.Id == @"task|\GoneTask");
        Assert.Equal(FindingGroups.OrphanEntries, a.Group);
        Assert.Equal([OrphanRules.OrphanReason], a.Reasons);
        Assert.True(a.Preselected);
        Assert.NotNull(a.Change);
        var b = Assert.Single(all, f => f.Id == @"task|\ComTask");
        Assert.Contains("keine Programmaktion", b.Reasons);
        Assert.Null(b.Group);
    }

    [Fact]
    public void Missing_exe_under_windows_is_not_orphan()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Odd", @"%SystemRoot%\gone_ww.exe");
        Assert.DoesNotContain(Autostarts(), f => f.Group == FindingGroups.OrphanEntries);

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(OrphanRules.IsOrphan(Path.Combine(win, "gone_ww.exe"), _files, win));
        Assert.True(OrphanRules.IsOrphan(Gone("x.exe"), _files, win));
    }

    [Fact]
    public void Network_drive_counts_as_not_ready()
    {
        var calls = 0;
        Assert.False(WinFileOps.VolumeUsable(DriveType.Network, () => { calls++; return true; }));
        Assert.Equal(0, calls); // never asks a network drive (IsReady can hang)
        Assert.True(WinFileOps.VolumeUsable(DriveType.Fixed, () => true));
        Assert.False(WinFileOps.VolumeUsable(DriveType.Fixed, () => false));
    }

    // --- per-user variables come from the interactive user's profile (elevated as another account) ---

    string InUserLocal(string vendor, string file, bool create)
    {
        var path = Path.Combine(_root, "AppData", "Local", vendor, file);
        if (create) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1]); }
        return path;
    }

    [Fact]
    public void NormalizeForUser_uses_the_interactive_profile()
    {
        Assert.Equal(Path.Combine(_root, "AppData", "Local", "V", "a.exe"), PathNormalizer.NormalizeForUser(@"%LOCALAPPDATA%\V\a.exe", _user));
        Assert.Equal(Path.Combine(_root, "AppData", "Roaming", "a.exe"), PathNormalizer.NormalizeForUser(@"%appdata%\a.exe", _user));
        Assert.Equal(Path.Combine(_root, "a.exe"), PathNormalizer.NormalizeForUser(@"%USERPROFILE%\a.exe", _user));
        Assert.Equal(Path.Combine(_root, "a.exe"), PathNormalizer.NormalizeForUser(@"%HOMEDRIVE%%HOMEPATH%\a.exe", _user));
        Assert.Equal(Path.Combine(_user.TempDir, "a.exe"), PathNormalizer.NormalizeForUser(@"%TEMP%\a.exe", _user));
        Assert.Equal(Path.Combine(_user.TempDir, "a.exe"), PathNormalizer.NormalizeForUser(@"%TMP%\a.exe", _user));
        // system variables still come from the process
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "a.exe"),
            PathNormalizer.NormalizeForUser(@"%SystemRoot%\a.exe", _user), ignoreCase: true);
    }

    [Fact]
    public void Autostart_in_interactive_users_LocalAppData_is_not_orphan()
    {
        var vendor = "WwVendor" + Guid.NewGuid().ToString("N");
        InUserLocal(vendor, "app.exe", create: true); // exists only in the interactive profile
        _reg.Seed(Hive.CurrentUser, Run, "App", $"\"%LOCALAPPDATA%\\{vendor}\\app.exe\" /tray");

        var f = Assert.Single(Autostarts());
        Assert.Null(f.Group);
        Assert.DoesNotContain(OrphanRules.OrphanReason, f.Reasons);
    }

    [Fact]
    public void Autostart_missing_in_interactive_users_LocalAppData_is_orphan()
    {
        var vendor = "WwVendor" + Guid.NewGuid().ToString("N");
        _reg.Seed(Hive.CurrentUser, Run, "App", $"\"%LOCALAPPDATA%\\{vendor}\\app.exe\"");

        var f = Assert.Single(Autostarts());
        Assert.Equal(FindingGroups.OrphanEntries, f.Group);
    }

    [Fact]
    public void Path_in_another_profile_or_with_unresolved_variable_is_unknown()
    {
        // the fake user's profile folder is _root, so its siblings count as other profiles
        var other = Path.Combine(Path.GetDirectoryName(_root)!, "ww-otheruser-" + Guid.NewGuid().ToString("N"), "a.exe");
        Assert.Equal(Presence.Unknown, ProgramPresence.Check(other, _files, _user));
        Assert.Equal(Presence.Missing, ProgramPresence.Check(other, _files)); // without a user nothing changes
        Assert.Equal(Presence.Unknown, ProgramPresence.Check(@"%WW_NO_SUCH_VAR%\a.exe", _files, _user));
        Assert.Equal(Presence.Missing, ProgramPresence.Check(Gone("a.exe"), _files, _user));

        _reg.Seed(Hive.LocalMachine, Run, "Other", $"\"{other}\"");
        _reg.Seed(Hive.LocalMachine, Run, "Var", @"%WW_NO_SUCH_VAR%\a.exe");
        Assert.DoesNotContain(Autostarts(), f => f.Group == FindingGroups.OrphanEntries);
    }

    [Fact]
    public void Task_with_user_variable_is_orphan_only_when_it_runs_as_the_interactive_user()
    {
        var vendor = "WwVendor" + Guid.NewGuid().ToString("N");
        var exe = $"%LOCALAPPDATA%\\{vendor}\\t.exe";
        _store.Add(@"\Mine", actionExe: exe, userId: _user.Sid);
        _store.Add(@"\MineByAuthor", actionExe: exe, author: _user.Sid);
        _store.Add(@"\Theirs", actionExe: exe, userId: "S-1-5-21-9-9-9-1234");
        _store.Add(@"\Nobody", actionExe: exe);
        _store.Add(@"\NamedStranger", actionExe: exe, userId: @"NOWHERE\ww-no-such-user");

        var all = new TaskScanner(_store, _sig, RuleSet.Default, _files, _user).Scan();
        Assert.Equal(FindingGroups.OrphanEntries, Assert.Single(all, f => f.Id == @"task|\Mine").Group);
        Assert.Equal(FindingGroups.OrphanEntries, Assert.Single(all, f => f.Id == @"task|\MineByAuthor").Group);
        foreach (var id in new[] { @"task|\Theirs", @"task|\Nobody", @"task|\NamedStranger" })
        {
            var f = Assert.Single(all, x => x.Id == id);
            Assert.Null(f.Group);
            Assert.False(f.Preselected);
        }

        // present in the interactive profile: not orphaned
        InUserLocal(vendor, "t.exe", create: true);
        var again = new TaskScanner(_store, _sig, RuleSet.Default, _files, _user).Scan();
        Assert.Null(Assert.Single(again, f => f.Id == @"task|\Mine").Group);
    }
}
