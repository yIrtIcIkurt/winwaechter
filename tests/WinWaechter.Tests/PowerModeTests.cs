using Microsoft.Win32;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PowerModeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-power-" + Guid.NewGuid().ToString("N"));
    readonly FakePowerModes _modes = new();

    public PowerModeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    [Fact]
    public void Apply_and_undo_switch_mode()
    {
        var custom = Guid.Parse("11111111-2222-3333-4444-555555555555");
        _modes.Seed(PowerSource.Ac, custom);
        var c = new PowerModeChange(_modes, PowerSource.Ac, PowerModeIds.BestEfficiency);
        Assert.Equal("powermode", c.Kind);
        Assert.Equal("Energiemodus (Netzbetrieb)", c.Target);
        Assert.Equal("Energiemodus (Netzbetrieb) auf „Beste Energieeffizienz“", c.Description);

        var rec = c.Apply(Ctx());
        Assert.Equal(PowerModeIds.BestEfficiency, _modes.Get(PowerSource.Ac));
        Assert.Equal("ac", rec.Before["source"]);
        Assert.Equal(custom.ToString("D"), rec.Before["mode"]);
        Assert.Equal(PowerModeIds.BestEfficiency.ToString("D"), rec.After["mode"]);

        c.Undo(rec, Ctx());
        Assert.Equal(custom, _modes.Get(PowerSource.Ac));

        var dc = new PowerModeChange(_modes, PowerSource.Dc, PowerModeIds.BestPerformance);
        Assert.Equal("Energiemodus (Akku)", dc.Target);
        Assert.Equal("dc", dc.Apply(Ctx()).Before["source"]);
    }

    [Fact]
    public void Drift_is_detected()
    {
        var c = new PowerModeChange(_modes, PowerSource.Ac, PowerModeIds.BestPerformance);
        var rec = c.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        _modes.Seed(PowerSource.Ac, PowerModeIds.Balanced);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
        _modes.Seed(PowerSource.Ac, null);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
    }

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        Assert.Throws<ArgumentException>(() =>
            new PowerModeChange(_modes, PowerSource.Ac, Guid.Parse("11111111-2222-3333-4444-555555555555")));

        var good = new PowerModeChange(_modes, PowerSource.Dc, PowerModeIds.BestEfficiency).Apply(Ctx());
        var restored = PowerModeChange.FromRecord(good, _modes);
        Assert.Equal("Energiemodus (Akku)", restored.Target);

        var badTarget = good with { After = new Dictionary<string, string?> { ["mode"] = "11111111-2222-3333-4444-555555555555" } };
        Assert.Throws<InvalidOperationException>(() => PowerModeChange.FromRecord(badTarget, _modes));

        var badSource = good with { Before = new Dictionary<string, string?> { ["source"] = "x", ["mode"] = PowerModeIds.Balanced.ToString("D") } };
        Assert.Throws<InvalidOperationException>(() => PowerModeChange.FromRecord(badSource, _modes));

        var badBefore = good with { Before = new Dictionary<string, string?> { ["source"] = "dc", ["mode"] = "not-a-guid" } };
        var c = PowerModeChange.FromRecord(badBefore, _modes);
        _modes.SetCalls.Clear();
        Assert.Throws<InvalidOperationException>(() => c.Undo(badBefore, Ctx()));
        Assert.Empty(_modes.SetCalls);
    }

    [Fact]
    public void Labels_for_known_and_custom()
    {
        Assert.Equal("Beste Energieeffizienz", PowerModeIds.Label(PowerModeIds.BestEfficiency));
        Assert.Equal("Ausgewogen", PowerModeIds.Label(PowerModeIds.Balanced));
        Assert.Equal("Beste Leistung", PowerModeIds.Label(PowerModeIds.BestPerformance));
        Assert.Equal("Benutzerdefiniert", PowerModeIds.Label(Guid.NewGuid()));
        Assert.Equal(3, PowerModeIds.All.Count);
    }

    [Fact]
    public void Registry_modes_default_to_balanced_and_unparsable_is_null()
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        var reg = new FakeRegistry();
        var p = new WinPowerModes(reg);
        Assert.Equal(PowerModeIds.Balanced, p.Get(PowerSource.Ac));
        reg.Seed(Hive.LocalMachine, key, "ActiveOverlayDcPowerScheme", "kaputt", RegistryValueKind.String);
        Assert.Null(p.Get(PowerSource.Dc));
        reg.Seed(Hive.LocalMachine, key, "ActiveOverlayAcPowerScheme", PowerModeIds.BestPerformance.ToString("D"), RegistryValueKind.String);
        Assert.Equal(PowerModeIds.BestPerformance, p.Get(PowerSource.Ac));
    }
}
