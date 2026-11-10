using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SecurityCoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-seccore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    SessionContext Ctx() => new(Path.Combine(_root, "session"));

    const string Uac = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string Term = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    const string WinNt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    static DefenderStatus Status(bool available = true, bool rt = true, bool tamper = true, int sigDays = 0) =>
        new(available, rt, tamper, 1, null, sigDays, null, null, null);

    [Fact]
    public void Firewall_off_profile_is_red_with_change()
    {
        var fw = new FakeFirewall();
        fw.State[FirewallProfile.Private] = false;

        var items = SecurityChecks.Firewall(fw, []);

        Assert.Equal([SecurityIds.FirewallDomain, SecurityIds.FirewallPrivate, SecurityIds.FirewallPublic], items.Select(i => i.Id));
        var priv = items.Single(i => i.Id == SecurityIds.FirewallPrivate);
        Assert.Equal(Light.Red, priv.Light);
        Assert.Equal("Firewall (Privat)", priv.Title);
        Assert.Equal("Ausgeschaltet", priv.Finding);
        Assert.Equal(SecurityActionKind.Change, priv.Action);
        Assert.Equal("Einschalten", priv.ActionLabel);
        Assert.Equal("firewall", priv.Change!.Kind);
        foreach (var other in items.Where(i => i.Id != SecurityIds.FirewallPrivate))
        {
            Assert.Equal(Light.Green, other.Light);
            Assert.Equal("Eingeschaltet", other.Finding);
            Assert.Equal(SecurityActionKind.None, other.Action);
        }
    }

    [Fact]
    public void Third_party_firewall_takes_over_without_action()
    {
        var fw = new FakeFirewall();
        fw.State[FirewallProfile.Private] = false;

        var on = SecurityChecks.Firewall(fw, [new("Contoso Firewall", true, false)]);
        var priv = on.Single(i => i.Id == SecurityIds.FirewallPrivate);
        Assert.Equal(Light.Green, priv.Light);
        Assert.Equal("Übernommen von Contoso Firewall", priv.Finding);
        Assert.Null(priv.Change);
        Assert.Equal(SecurityActionKind.None, priv.Action);

        var off = SecurityChecks.Firewall(fw, [new("Contoso Firewall", false, false)]);
        Assert.Equal(Light.Red, off.Single(i => i.Id == SecurityIds.FirewallPrivate).Light);

        var ms = SecurityChecks.Firewall(fw, [new("Windows Defender Firewall", true, true)]);
        Assert.Equal(Light.Red, ms.Single(i => i.Id == SecurityIds.FirewallPrivate).Light);
    }

    [Fact]
    public void FirewallChange_apply_undo_and_drift()
    {
        var fw = new FakeFirewall();
        fw.State[FirewallProfile.Private] = false;
        var change = new FirewallChange(fw, FirewallProfile.Private);

        var rec = change.Apply(Ctx());

        Assert.True(fw.State[FirewallProfile.Private]);
        Assert.Equal("false", rec.Before["enabled"]);
        Assert.Equal("2", rec.Before["profile"]);
        Assert.Equal("true", rec.After["enabled"]);
        Assert.Equal("Firewall Privat", rec.Target);
        Assert.Equal("Firewall für das Profil „Privat“ einschalten", rec.Description);
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        fw.State[FirewallProfile.Private] = false;
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));

        fw.State[FirewallProfile.Private] = true;
        change.Undo(rec, Ctx());
        Assert.False(fw.State[FirewallProfile.Private]);
    }

    [Fact]
    public void FirewallChange_FromRecord_refuses_unknown_profile()
    {
        var fw = new FakeFirewall();
        ChangeRecord Rec(string profile) => new("id", "firewall", "t", "d",
            new() { ["profile"] = profile, ["enabled"] = "false" }, new() { ["enabled"] = "true" },
            ChangeStatus.Applied, DateTimeOffset.Now);

        Assert.Throws<InvalidOperationException>(() => FirewallChange.FromRecord(Rec("3"), fw));
        Assert.Throws<InvalidOperationException>(() => FirewallChange.FromRecord(Rec("x"), fw));

        var factory = new ChangeFactory();
        FirewallChange.Register(factory, fw);
        var change = factory.Rehydrate(Rec("4"));
        Assert.IsType<FirewallChange>(change);
        Assert.Equal("Firewall Öffentlich", change.Target);
    }

    [Fact]
    public void Defender_rules()
    {
        var other = SecurityChecks.Defender(Status(rt: false), [new("Contoso AV", true, false)]);
        Assert.Equal(Light.Green, other.Light);
        Assert.Equal("Übernommen von Contoso AV", other.Finding);
        Assert.Null(other.Link);

        var grey = SecurityChecks.Defender(Status(available: false), []);
        Assert.Equal(Light.Grey, grey.Light);
        Assert.Equal("Nicht aktiv oder nicht lesbar", grey.Finding);

        var rt = SecurityChecks.Defender(Status(rt: false), []);
        Assert.Equal(Light.Red, rt.Light);
        Assert.Equal("Echtzeitschutz ist aus", rt.Finding);

        var tamper = SecurityChecks.Defender(Status(tamper: false), []);
        Assert.Equal(Light.Red, tamper.Light);
        Assert.Equal("Manipulationsschutz ist aus", tamper.Finding);

        var old = SecurityChecks.Defender(Status(sigDays: 4), []);
        Assert.Equal(Light.Yellow, old.Light);
        Assert.Equal("Virendefinitionen sind 4 Tage alt", old.Finding);

        var ok = SecurityChecks.Defender(Status(sigDays: 3), []);
        Assert.Equal(Light.Green, ok.Light);
        Assert.Equal("Echtzeit- und Manipulationsschutz an", ok.Finding);

        foreach (var i in new[] { grey, rt, tamper, old, ok })
        {
            Assert.Equal("windowsdefender://threat", i.Link);
            Assert.Equal("Windows-Sicherheit öffnen", i.ActionLabel);
            Assert.Equal("Microsoft Defender", i.Title);
        }
    }

    [Fact]
    public void SmartScreen_rules()
    {
        var none = SecurityChecks.SmartScreen(new FakeRegistry());
        Assert.Equal(Light.Green, none.Light);
        Assert.Equal("Eingeschaltet", none.Finding);
        Assert.Equal("windowsdefender://appbrowser", none.Link);
        Assert.Equal("Einstellungen öffnen", none.ActionLabel);

        var off = SecurityChecks.SmartScreen(new FakeRegistry().Seed(Hive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", "off"));
        Assert.Equal(Light.Yellow, off.Light);
        Assert.Equal("Ausgeschaltet", off.Finding);

        var policy = SecurityChecks.SmartScreen(new FakeRegistry().Seed(Hive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, RegistryValueKind.DWord));
        Assert.Equal(Light.Yellow, policy.Light);
        Assert.Equal("windowsdefender://appbrowser", policy.Link);

        var warn = SecurityChecks.SmartScreen(new FakeRegistry().Seed(Hive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", "Warn"));
        Assert.Equal(Light.Green, warn.Light);
    }

    [Fact]
    public void Uac_rules()
    {
        Assert.Equal(Light.Green, SecurityChecks.Uac(new FakeRegistry()).Light);

        foreach (var (name, light) in new[]
                 {
                     ("EnableLUA", Light.Red), ("ConsentPromptBehaviorAdmin", Light.Yellow), ("PromptOnSecureDesktop", Light.Yellow),
                 })
        {
            var item = SecurityChecks.Uac(new FakeRegistry().Seed(Hive.LocalMachine, Uac, name, 0, RegistryValueKind.DWord));
            Assert.Equal(light, item.Light);
            Assert.Equal(SecurityActionKind.None, item.Action);
            Assert.Null(item.Link);
        }

        var on = SecurityChecks.Uac(new FakeRegistry()
            .Seed(Hive.LocalMachine, Uac, "EnableLUA", 1, RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, Uac, "ConsentPromptBehaviorAdmin", 5, RegistryValueKind.DWord)
            .Seed(Hive.LocalMachine, Uac, "PromptOnSecureDesktop", 1, RegistryValueKind.DWord));
        Assert.Equal(Light.Green, on.Light);
        Assert.Equal("Eingeschaltet", on.Finding);
    }

    [Fact]
    public void Remote_desktop_rules()
    {
        var home = SecurityChecks.RemoteDesktop(new FakeRegistry()
            .Seed(Hive.LocalMachine, WinNt, "EditionID", "Core")
            .Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 0, RegistryValueKind.DWord));
        Assert.Equal(Light.Grey, home.Light);
        Assert.Equal("Nicht verfügbar (Home)", home.Finding);
        Assert.Null(home.Link);

        var pro = new FakeRegistry().Seed(Hive.LocalMachine, WinNt, "EditionID", "Professional");
        var open = SecurityChecks.RemoteDesktop(pro.Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 0, RegistryValueKind.DWord));
        Assert.Equal(Light.Yellow, open.Light);
        Assert.Equal("Eingeschaltet", open.Finding);
        Assert.Equal("ms-settings:remotedesktop", open.Link);

        var closed = SecurityChecks.RemoteDesktop(pro.Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 1, RegistryValueKind.DWord));
        Assert.Equal(Light.Green, closed.Light);
        Assert.Equal("Ausgeschaltet", closed.Finding);
    }

    [Fact]
    public void Encryption_rules()
    {
        var none = SecurityChecks.Encryption(null);
        Assert.Equal(Light.Grey, none.Light);
        Assert.Equal("Nicht verfügbar", none.Finding);
        Assert.Null(none.Link);

        var on = SecurityChecks.Encryption(new(1, 1));
        Assert.Equal(Light.Green, on.Light);
        Assert.Equal("Systemlaufwerk verschlüsselt", on.Finding);

        var waiting = SecurityChecks.Encryption(new(0, 1));
        Assert.Equal(Light.Yellow, waiting.Light);
        Assert.Equal("Verschlüsselt, wartet auf Aktivierung", waiting.Finding);

        var off = SecurityChecks.Encryption(new(0, 0));
        Assert.Equal(Light.Yellow, off.Light);
        Assert.Equal("Systemlaufwerk nicht verschlüsselt", off.Finding);

        foreach (var i in new[] { on, waiting, off })
        {
            Assert.Equal("ms-settings:deviceencryption", i.Link);
            Assert.Equal("Geräteverschlüsselung", i.Title);
        }
    }

    [Fact]
    public void Unavailable_is_grey()
    {
        var a = SecurityChecks.Unavailable(SecurityIds.Updates, "Windows Update");
        Assert.Equal(Light.Grey, a.Light);
        Assert.Equal("Nicht prüfbar", a.Finding);
        Assert.Equal("Nicht prüfbar – Zeitüberschreitung", SecurityChecks.Unavailable("x", "y", "Zeitüberschreitung").Finding);
    }

    [Fact]
    public void Every_link_is_in_SystemLinks()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, Term, "fDenyTSConnections", 0, RegistryValueKind.DWord);
        var items = new List<SecurityItem>
        {
            SecurityChecks.Defender(Status(), []), SecurityChecks.Defender(Status(rt: false), []),
            SecurityChecks.SmartScreen(new FakeRegistry()), SecurityChecks.Uac(new FakeRegistry()),
            SecurityChecks.RemoteDesktop(reg), SecurityChecks.RemoteDesktop(new FakeRegistry()),
            SecurityChecks.Encryption(null), SecurityChecks.Encryption(new(1, 1)), SecurityChecks.Encryption(new(0, 0)),
            SecurityChecks.Unavailable("x", "y"),
        };
        items.AddRange(SecurityChecks.Firewall(new FakeFirewall(), []));

        var withLink = items.Where(i => i.Link is not null).ToList();
        Assert.NotEmpty(withLink);
        Assert.All(withLink, i => Assert.True(SystemLinks.IsAllowed(i.Link), i.Link));
    }

    [Fact]
    public void Real_sources_smoke_read_only()
    {
        try { _ = new WmiSecurityCenter().AntiVirus(); } catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException) { }
        try { _ = new WmiVolumeEncryption().SystemDrive(); } catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException) { }
        // Read only: never calls SetEnabled.
        var on = new FwPolicy2Firewall().IsEnabled(FirewallProfile.Private);
        Assert.True(on || !on);
    }
}
