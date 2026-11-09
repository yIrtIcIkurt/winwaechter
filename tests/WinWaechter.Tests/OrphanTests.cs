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
    public void Unc_or_unready_drive_is_not_orphan()
    {
        _reg.Seed(Hive.CurrentUser, Run, "Nas", @"\nas\x\a.exe");
        var root = Path.GetPathRoot(_root)!;
        _files.NotReady.Add(root);
        _reg.Seed(Hive.CurrentUser, Run, "Gone", $"\"{Gone("g.exe")}\"");

        var all = Autostarts();
        Assert.Equal(2, all.Count);
        Assert.All(all, f => Assert.Null(f.Group));
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

        var all = new TaskScanner(_store, _sig, RuleSet.Default, _files).Scan();
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
}
