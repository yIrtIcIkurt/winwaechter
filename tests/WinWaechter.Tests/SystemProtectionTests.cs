using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SystemProtectionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-sysprotect-" + Guid.NewGuid().ToString("N"));
    readonly FakeServiceControl _sc = new();
    readonly FakeRestoreConfig _cfg = new() { State = false };
    readonly FakeRegistry _reg = new();

    public SystemProtectionTests()
    {
        Directory.CreateDirectory(_root);
        _sc.Add("VSS", StartMode.Disabled, running: false).Add("swprv", StartMode.Manual, running: false);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    SecurityItem Check() => SystemProtection.Check(_sc, _cfg, _reg, new FakePolicyOwnership(), @"C:\");

    SystemProtectChange Change(params string[] services) => new(_sc, _cfg, @"C:\", services);

    static ChangeRecord Rec(string drive, string services) =>
        new("id", "sysprotect", $"Computerschutz {drive}", "x",
            new() { ["drive"] = drive, ["services"] = services }, new(), ChangeStatus.Applied, DateTimeOffset.Now);

    [Fact]
    public void Disabled_service_is_set_to_manual_and_protection_enabled()
    {
        var c = Change("VSS");
        var rec = c.Apply(Ctx());

        Assert.Equal(StartMode.Manual, _sc.Get("VSS")!.Mode);
        Assert.Equal([("VSS", StartMode.Manual, false)], _sc.SetStartModeCalls);
        Assert.Equal([@"C:\"], _cfg.EnableCalls);
        Assert.Equal("sysprotect", rec.Kind);
        Assert.Equal(@"Computerschutz C:\", rec.Target);
        Assert.Equal(@"C:\", rec.Before["drive"]);
        Assert.Equal("VSS", rec.Before["services"]);
        Assert.Equal("false", rec.Before["protection"]);
        Assert.Equal("Manual", rec.After["mode"]);
        Assert.Equal("true", rec.After["protection"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));

        _sc.SetStartMode("VSS", StartMode.Disabled, false);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
    }

    [Fact]
    public void Apply_only_touches_services_that_are_still_disabled_and_skips_enable_when_on()
    {
        _cfg.State = true;
        _sc.SetStartMode("VSS", StartMode.Manual, false);
        _sc.SetStartModeCalls.Clear();
        var rec = Change("VSS", "swprv").Apply(Ctx());
        Assert.Empty(_sc.SetStartModeCalls);
        Assert.Empty(_cfg.EnableCalls);
        Assert.Equal("", rec.Before["services"]);
    }

    [Fact]
    public void Undo_restores_only_the_service()
    {
        var c = Change("VSS");
        var rec = c.Apply(Ctx());
        c.Undo(rec, Ctx());

        Assert.Equal(StartMode.Disabled, _sc.Get("VSS")!.Mode);
        Assert.Equal(StartMode.Manual, _sc.Get("swprv")!.Mode);   // was never Disabled, not touched
        Assert.DoesNotContain(_sc.SetStartModeCalls, x => x.Name == "swprv");
    }

    [Fact]
    public void Undo_never_switches_protection_off()
    {
        var c = Change("VSS");
        var rec = c.Apply(Ctx());
        c.Undo(rec, Ctx());

        Assert.True(_cfg.State);
        Assert.Single(_cfg.EnableCalls);
    }

    [Fact]
    public void Policy_blocks_fix()
    {
        foreach (var name in new[] { "DisableSR", "DisableConfig" })
        {
            var reg = new FakeRegistry().Seed(Hive.LocalMachine, SystemProtection.PolicyKey, name, 1, RegistryValueKind.DWord);
            var item = SystemProtection.Check(_sc, _cfg, reg, new FakePolicyOwnership(), @"C:\");
            Assert.Equal(Light.Yellow, item.Light);
            Assert.Equal("Von einer Richtlinie festgelegt", item.Finding);
            Assert.Null(item.Change);
            Assert.Equal(SecurityActionKind.None, item.Action);
        }
        Assert.Empty(_cfg.EnableCalls);
    }

    [Fact]
    public void Change_is_marked_partial()
    {
        var c = Change("VSS");
        Assert.Equal(UndoLimits.Partial, c.UndoLimit);
        Assert.Equal(@"Computerschutz für C:\ einschalten (Nur teilweise rückgängig)", ChangeText.ConfirmLine(c));
        Assert.EndsWith("(Nur teilweise rückgängig)", ChangeText.ConfirmLine(c));
    }

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        var ok = SystemProtectChange.FromRecord(Rec(@"D:\", "VSS,swprv"), _sc, _cfg);
        Assert.Equal(@"Computerschutz D:\", ok.Target);
        Assert.Equal("VSS,swprv", ok.RetryData()!.Value.Before["services"]);

        Assert.Throws<InvalidOperationException>(() => SystemProtectChange.FromRecord(Rec(@"C:\", "wuauserv"), _sc, _cfg));
        Assert.Throws<InvalidOperationException>(() => SystemProtectChange.FromRecord(Rec(@"C:\", "VSS,wuauserv"), _sc, _cfg));
        Assert.Throws<InvalidOperationException>(() => SystemProtectChange.FromRecord(Rec(@"C:\..", "VSS"), _sc, _cfg));
        Assert.Throws<InvalidOperationException>(() => SystemProtectChange.FromRecord(Rec(@"c:\", "VSS"), _sc, _cfg));
        Assert.Throws<InvalidOperationException>(() => SystemProtectChange.FromRecord(Rec(@"C:", "VSS"), _sc, _cfg));
    }

    [Fact]
    public void RetryData_rebuilds_through_FromRecord()
    {
        var (before, after) = Change("VSS", "swprv").RetryData()!.Value;
        var rec = new ChangeRecord("id", "sysprotect", "t", "x", before, after, ChangeStatus.Failed, DateTimeOffset.Now);
        var again = SystemProtectChange.FromRecord(rec, _sc, _cfg);
        Assert.Equal(@"Computerschutz C:\", again.Target);
        Assert.Equal("VSS,swprv", again.RetryData()!.Value.Before["services"]);
    }

    [Fact]
    public void Failed_enable_propagates_and_plan_keeps_services_for_undo()
    {
        _cfg.EnableError = new InvalidOperationException("Der Computerschutz ließ sich nicht einschalten (Code 5).");
        var c = Change("VSS");
        var plan = c.Plan(Ctx());
        Assert.Equal(ChangeStatus.Pending, plan.Status);
        Assert.Equal("VSS", plan.Before["services"]);
        Assert.Throws<InvalidOperationException>(() => c.Apply(Ctx()));
        c.Undo(plan, Ctx());
        Assert.Equal(StartMode.Disabled, _sc.Get("VSS")!.Mode);
    }

    [Fact]
    public void Check_rows()
    {
        // red: protection off, one service disabled
        var red = Check();
        Assert.Equal(Light.Red, red.Light);
        Assert.Equal("Ausgeschaltet", red.Finding);
        Assert.Equal("Ohne Computerschutz gibt es keine Wiederherstellungspunkte, auch nicht vor Änderungen durch WinWächter.", red.Explanation);
        Assert.Equal(SecurityActionKind.Change, red.Action);
        Assert.Equal("Einschalten", red.ActionLabel);
        Assert.True(red.Recommended);
        Assert.IsType<SystemProtectChange>(red.Change);
        Assert.Equal("VSS", red.Change!.RetryData()!.Value.Before["services"]);

        // red: protection on, service disabled
        _cfg.State = true;
        Assert.Equal("Ein nötiger Dienst ist deaktiviert", Check().Finding);

        // green
        _sc.SetStartMode("VSS", StartMode.Manual, false);
        var green = Check();
        Assert.Equal(Light.Green, green.Light);
        Assert.Null(green.Change);

        // protection off, services fine: still red with a fix, no services listed
        _cfg.State = false;
        var offOnly = Check();
        Assert.Equal(Light.Red, offOnly.Light);
        Assert.Equal("", offOnly.Change!.RetryData()!.Value.Before["services"]);

        // grey: unreadable state, services fine
        _cfg.State = null;
        var grey = Check();
        Assert.Equal(Light.Grey, grey.Light);
        Assert.Equal("Nicht prüfbar", grey.Finding);
        Assert.Null(grey.Change);

        // unreadable state but a disabled service: the service fix stays
        _sc.SetStartMode("swprv", StartMode.Disabled, false);
        Assert.Equal(Light.Red, Check().Light);
    }

    [Fact]
    public async Task Row_appears_in_the_check_only_with_both_sources()
    {
        var paths = new CleanerPaths(Path.Combine(_root, "L"), Path.Combine(_root, "R"), Path.Combine(_root, "win"),
            Path.Combine(_root, "pd"), Path.Combine(_root, "pf"), Path.Combine(_root, "pf86"));
        SecuritySources Make(ISystemRestoreConfig? cfg, IServiceControl? services) => new(new FakeFirewall(), new FakeSecurityCenter(),
            new FakeDefender(), _reg, new FakeVolumeEncryption { State = new(1, 0) }, new FakeUpdateSearch(), new FakeListeners(),
            new FakeProcessList(), new FakeSignatures(), new BrowserExtensionScanner(paths, _reg),
            WindowsDir: @"C:\Windows", RestoreConfig: cfg, Services: services);

        var without = await new SecurityCheck(Make(null, _sc)).RunAsync(CancellationToken.None);
        Assert.DoesNotContain(without, i => i.Id == SecurityIds.SystemProtection);
        without = await new SecurityCheck(Make(_cfg, null)).RunAsync(CancellationToken.None);
        Assert.DoesNotContain(without, i => i.Id == SecurityIds.SystemProtection);

        var with = await new SecurityCheck(Make(_cfg, _sc)).RunAsync(CancellationToken.None);
        var ids = with.Select(i => i.Id).ToList();
        Assert.Equal(ids.IndexOf(SecurityIds.Updates) + 1, ids.IndexOf(SecurityIds.SystemProtection));
        Assert.Equal(Light.Red, with.Single(i => i.Id == SecurityIds.SystemProtection).Light);

        _cfg.ReadError = new InvalidOperationException("WMI");
        var broken = await new SecurityCheck(Make(_cfg, _sc)).RunAsync(CancellationToken.None);
        Assert.Equal(Light.Grey, broken.Single(i => i.Id == SecurityIds.SystemProtection).Light);
    }
}
