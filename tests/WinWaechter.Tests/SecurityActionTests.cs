using WinWaechter.Core.Actions;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class SecurityActionTests
{
    static SecurityActionSet Set(FakeDefender? d = null, FakeUpdateInstaller? u = null, FakeRestarter? r = null) =>
        SecurityActionSet.Create(d ?? new FakeDefender(), u ?? new FakeUpdateInstaller(), r ?? new FakeRestarter());

    static DefenderStatus Status(bool rt = true, bool tamper = true, int sigDays = 0) =>
        new(true, rt, tamper, 0, null, sigDays, null, null, null);

    static UpdateFacts Facts(string id, bool auto = true, bool browseOnly = false, bool eula = true, int type = 1, params string[] cats) =>
        new(id, id, auto, browseOnly, eula, cats, type);

    [Fact]
    public void Only_automatic_updates_are_selected()
    {
        var all = new[]
        {
            Facts("ok"),
            Facts("optional", auto: false),
            Facts("browse", browseOnly: true),
            Facts("upgrade", cats: ["Security Updates", "Upgrades"]),
            Facts("driver", type: 2),
            Facts("eula", eula: false),
        };
        Assert.Equal(["ok"], WuaUpdateInstaller.Select(all).Select(u => u.Id));
        Assert.Equal(1, WuaUpdateInstaller.NeedingLicence(all));
    }

    [Fact]
    public async Task Realtime_enable_succeeds()
    {
        var d = new FakeDefender { RealTime = false };
        var o = await Set(d).EnableRealTime.RunAsync(null, CancellationToken.None);
        Assert.True(o.Success);
        Assert.Contains("enable-realtime", d.Calls);
    }

    [Fact]
    public async Task Realtime_still_off_after_enable_gives_message_and_link()
    {
        var d = new FakeDefender { RealTime = false, EnableBlocked = true };
        var o = await Set(d).EnableRealTime.RunAsync(null, CancellationToken.None);
        Assert.False(o.Success);
        Assert.Equal("Windows hat das Einschalten blockiert, vermutlich durch den Manipulationsschutz. Bitte schalten Sie den Echtzeitschutz in der Windows-Sicherheit ein.", o.Message);
        Assert.Equal(SystemLinks.DefenderThreat, o.Link);
    }

    [Fact]
    public async Task Signature_update_failure_names_the_code()
    {
        var o = await Set(new FakeDefender { UpdateExit = 2 }).UpdateSignatures.RunAsync(null, CancellationToken.None);
        Assert.False(o.Success);
        Assert.Contains("(Code 2)", o.Message);
        Assert.True((await Set().UpdateSignatures.RunAsync(null, CancellationToken.None)).Success);
    }

    [Fact]
    public void Restart_uses_only_fixed_arguments()
    {
        var runner = new FakeProcessRunner();
        new WinRestarter(runner, @"C:\Windows").Restart();
        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\Windows\System32\shutdown.exe", call.Exe);
        Assert.Equal("/r /t 0", call.Args);
    }

    [Fact]
    public async Task Restart_action_restarts_and_cancel_does_not()
    {
        var r = new FakeRestarter();
        var set = Set(r: r);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set.Restart.RunAsync(null, cts.Token));
        Assert.Equal(0, r.Restarts);
        await set.Restart.RunAsync(null, CancellationToken.None);
        Assert.Equal(1, r.Restarts);
    }

    [Fact]
    public void Rows_carry_actions()
    {
        var a = Set();
        var off = SecurityChecks.Defender(Status(rt: false), [], a);
        Assert.Equal(SecurityActionKind.Action, off.Action);
        Assert.Same(a.EnableRealTime, off.Act);
        Assert.Equal("Einschalten", off.ActionLabel);

        var tamper = SecurityChecks.Defender(Status(tamper: false), [], a);
        Assert.Equal(SecurityActionKind.Link, tamper.Action);
        Assert.Null(tamper.Act);
        Assert.Contains("Den Manipulationsschutz lassen Programme absichtlich nicht einschalten – nur Sie selbst in der Windows-Sicherheit.", tamper.Explanation);

        var old = SecurityChecks.Defender(Status(sigDays: 4), [], a);
        Assert.Same(a.UpdateSignatures, old.Act);
        Assert.Equal(SecurityActionKind.Action, old.Action);
        Assert.Null(SecurityChecks.Defender(Status(sigDays: 4), []).Act);

        var pending = SecurityChecks.Updates(false, new(2, null, false), a);
        Assert.Same(a.InstallUpdates, pending.Act);
        Assert.Equal(SecurityActionKind.Action, pending.Action);
        var reboot = SecurityChecks.Updates(true, new(2, null, false), a);
        Assert.Same(a.Restart, reboot.Act);
        var none = SecurityChecks.Updates(false, new(0, null, false), a);
        Assert.Null(none.Act);
        Assert.Equal(SecurityActionKind.Link, none.Action);
        Assert.Null(SecurityChecks.Updates(false, null, a).Act);
        Assert.Null(SecurityChecks.Updates(false, new(2, null, false)).Act);
    }

    [Fact]
    public void Encryption_explains_manual_switch_on()
    {
        var row = SecurityChecks.Encryption(new(0, 0));
        Assert.Contains("Einschalten nur in Windows: Den Wiederherstellungsschlüssel müssen Sie selbst sichern.", row.Explanation);
    }

    [Fact]
    public async Task Install_updates_confirm_text_is_exact()
    {
        var a = Set();
        Assert.True(a.InstallUpdates.Final);
        Assert.Equal("Updates installieren", a.InstallUpdates.ButtonText);
        Assert.Equal("Updates lassen sich nicht über WinWächter zurücknehmen; einzelne können Sie in Windows unter Updateverlauf deinstallieren.", a.InstallUpdates.ConfirmText);
        Assert.Equal("Speichern Sie offene Arbeit. Der Rechner startet sofort neu.", a.Restart.ConfirmText);
        Assert.Null(a.UpdateSignatures.ConfirmText);

        var u = new FakeUpdateInstaller { Result = new(3, 0, 1, true) };
        var o = await Set(u: u).InstallUpdates.RunAsync(null, CancellationToken.None);
        Assert.True(o.Success);
        Assert.Equal("3 installiert, 1 nur in Windows Update installierbar. Ein Neustart ist nötig.", o.Message);
    }
}
