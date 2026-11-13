using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SecValueTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-secvalue-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();

    public SecValueTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    const string Explorer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string Term = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";

    [Fact]
    public void SmartScreen_apply_undo_and_drift_as_string()
    {
        var v = SecurityValues.SmartScreen;
        _reg.Seed(v.Hive, v.Key, v.Name, "Off", RegistryValueKind.String);
        var c = new SecValueChange(_reg, v, "SmartScreen einschalten (warnen)");
        Assert.Equal("secvalue", c.Kind);
        Assert.Equal(@"HKLM\" + Explorer + @"\SmartScreenEnabled", c.Target);

        var rec = c.Apply(Ctx());
        Assert.Equal("Warn", _reg.Get(v.Hive, v.Key, v.Name));
        Assert.Equal(RegistryValueKind.String, _reg.GetKind(v.Hive, v.Key, v.Name));
        Assert.Equal("Warn", rec.After["value"]);
        Assert.Equal("String", rec.After["kind"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));

        _reg.Seed(v.Hive, v.Key, v.Name, "Off", RegistryValueKind.String);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));

        _reg.Seed(v.Hive, v.Key, v.Name, "Warn", RegistryValueKind.String);
        c.Undo(rec, Ctx());
        Assert.Equal("Off", _reg.Get(v.Hive, v.Key, v.Name));

        // missing value: undo deletes it again
        var reg2 = new FakeRegistry();
        var c2 = new SecValueChange(reg2, v, "x");
        var rec2 = c2.Apply(Ctx());
        Assert.Equal("false", rec2.Before["existed"]);
        c2.Undo(rec2, Ctx());
        Assert.Null(reg2.GetKind(v.Hive, v.Key, v.Name));
    }

    [Fact]
    public void Uac_fix_touches_only_the_defect_value()
    {
        _reg.Seed(Hive.LocalMachine, UacKey, "EnableLUA", 1, RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, UacKey, "PromptOnSecureDesktop", 0, RegistryValueKind.DWord);

        var item = SecurityChecks.Uac(_reg);
        Assert.Equal("Admins werden nicht gefragt", item.Finding);
        Assert.Equal(SecurityActionKind.Change, item.Action);
        Assert.Equal("Beheben", item.ActionLabel);
        Assert.True(item.Recommended);
        Assert.Equal("Admins wieder fragen", item.Change!.Description);

        var rec = item.Change.Apply(Ctx());
        Assert.Equal(5, _reg.Get(Hive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin"));
        Assert.Equal(0, _reg.Get(Hive.LocalMachine, UacKey, "PromptOnSecureDesktop"));
        Assert.Equal(1, _reg.Get(Hive.LocalMachine, UacKey, "EnableLUA"));
        Assert.Single(_reg.SetCalls);

        // next defect after the first is fixed
        var next = SecurityChecks.Uac(_reg);
        Assert.Equal("Abfrage ohne sicheren Desktop", next.Finding);
        Assert.Equal("Abfrage auf dem sicheren Desktop", next.Change!.Description);

        item.Change.Undo(rec, Ctx());
        Assert.Equal(0, _reg.Get(Hive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin"));

        var lua = SecurityChecks.Uac(new FakeRegistry().Seed(Hive.LocalMachine, UacKey, "EnableLUA", 0, RegistryValueKind.DWord));
        Assert.Equal("Benutzerkontensteuerung einschalten", lua.Change!.Description);
        Assert.EndsWith("Wirkt erst nach einem Neustart.", lua.Explanation);
    }

    [Fact]
    public void Foreign_smartscreen_policy_gets_no_fix()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, PolicyKey, "EnableSmartScreen", 0, RegistryValueKind.DWord);

        var item = SecurityChecks.SmartScreen(reg, new FakePolicyOwnership());
        Assert.Equal(Light.Yellow, item.Light);
        Assert.Null(item.Change);
        Assert.Null(item.Link);
        Assert.Equal(SecurityActionKind.None, item.Action);
        Assert.Contains(PolicyValues.ForeignText, item.Finding);
        Assert.False(item.Recommended);

        // a policy we set ourselves is no policy to the user's fix either way: still no silent write
        var ours = SecurityChecks.SmartScreen(reg, new FakePolicyOwnership().Own(Hive.LocalMachine, PolicyKey, "EnableSmartScreen"));
        Assert.Null(ours.Change);
    }

    [Fact]
    public void SmartScreen_off_without_policy_gets_fix()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, Explorer, "SmartScreenEnabled", "Off");
        var item = SecurityChecks.SmartScreen(reg, new FakePolicyOwnership());
        Assert.Equal("Ausgeschaltet", item.Finding);
        Assert.Equal(SecurityActionKind.Change, item.Action);
        Assert.Equal("Einschalten", item.ActionLabel);
        Assert.Equal("SmartScreen einschalten (warnen)", item.Change!.Description);
        Assert.True(item.Recommended);
    }

    [Fact]
    public void Rdp_open_gets_switch_off()
    {
        var reg = new FakeRegistry()
            .Seed(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "Professional")
            .Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 0, RegistryValueKind.DWord);

        var item = SecurityChecks.RemoteDesktop(reg);
        Assert.Equal(SecurityActionKind.Change, item.Action);
        Assert.Equal("Ausschalten", item.ActionLabel);
        Assert.Equal("Remotedesktop ausschalten", item.Change!.Description);
        Assert.True(item.Recommended);

        item.Change.Apply(Ctx());
        Assert.Equal(1, reg.Get(Hive.LocalMachine, Term, "fDenyTSConnections"));

        var home = SecurityChecks.RemoteDesktop(new FakeRegistry()
            .Seed(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "Core")
            .Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 0, RegistryValueKind.DWord));
        Assert.Equal(Light.Grey, home.Light);
        Assert.Null(home.Change);
    }

    static ChangeRecord Record(string hive, string key, string name, string afterValue) =>
        new("id", "secvalue", "t", "d",
            new() { ["hive"] = hive, ["key"] = key, ["name"] = name, ["existed"] = "false" },
            new() { ["value"] = afterValue, ["kind"] = "String" }, ChangeStatus.Applied, DateTimeOffset.Now);

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        const string msg = "Dieser Wert gehört nicht zu den Sicherheits-Einstellungen – Rückgängig abgelehnt.";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SecValueChange.FromRecord(Record("HKLM", @"SOFTWARE\Evil", "SmartScreenEnabled", "Warn"), _reg));
        Assert.Equal(msg, ex.Message);
        Assert.Throws<InvalidOperationException>(() =>
            SecValueChange.FromRecord(Record("HKCU", Explorer, "SmartScreenEnabled", "Warn"), _reg));
        Assert.Throws<InvalidOperationException>(() =>
            SecValueChange.FromRecord(Record("HKLM", Explorer, "Other", "Warn"), _reg));

        // the target always comes from the list, never from the record
        var c = SecValueChange.FromRecord(Record("HKLM", Explorer, "SmartScreenEnabled", "Off"), _reg);
        var rec = c.Apply(Ctx());
        Assert.Equal("Warn", _reg.Get(Hive.LocalMachine, Explorer, "SmartScreenEnabled"));
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(Record("HKLM", Explorer, "SmartScreenEnabled", "Off")));
        Assert.Equal("Warn", rec.After["value"]);
    }

    [Fact]
    public void Registered_kind_rehydrates()
    {
        var f = new ChangeFactory();
        SecValueChange.Register(f, _reg);
        var v = SecurityValues.RdpDeny;
        _reg.Seed(v.Hive, v.Key, v.Name, 0, RegistryValueKind.DWord);
        var original = new SecValueChange(_reg, v, "Remotedesktop ausschalten");
        var rec = original.Apply(Ctx());

        var again = f.Rehydrate(rec);
        Assert.IsType<SecValueChange>(again);
        again.Undo(rec, Ctx());
        Assert.Equal(0, _reg.Get(v.Hive, v.Key, v.Name));
        Assert.Equal(("HKLM", v.Key, v.Name), (original.RetryData()!.Value.Before["hive"], original.RetryData()!.Value.Before["key"]!, original.RetryData()!.Value.Before["name"]!));
    }
}
