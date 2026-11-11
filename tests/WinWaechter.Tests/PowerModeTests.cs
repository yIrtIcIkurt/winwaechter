using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
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

    sealed class FakeApi
    {
        public readonly Dictionary<PowerSource, Guid> Modes = new()
        {
            [PowerSource.Ac] = PowerModeIds.Balanced,
            [PowerSource.Dc] = PowerModeIds.Balanced,
        };
        public readonly List<(PowerSource Source, Guid Mode)> Sets = [];
        public uint GetCode, SetCode;
        public bool Missing;

        public PowerModeApi Api => new(
            s => Missing ? throw new EntryPointNotFoundException() : (GetCode, Modes[s]),
            (s, g) =>
            {
                if (Missing) throw new EntryPointNotFoundException();
                Sets.Add((s, g));
                if (SetCode == 0) Modes[s] = g;
                return SetCode;
            });
    }

    [Fact]
    public void Get_and_set_use_the_power_mode_api_per_source()
    {
        var api = new FakeApi();
        api.Modes[PowerSource.Dc] = PowerModeIds.BestEfficiency;
        var p = new WinPowerModes(api.Api);
        Assert.True(p.Supported);
        Assert.Equal(PowerModeIds.Balanced, p.Get(PowerSource.Ac));
        Assert.Equal(PowerModeIds.BestEfficiency, p.Get(PowerSource.Dc));

        // either source is set directly, whichever is active; Windows applies the other at the next switch
        p.Set(PowerSource.Ac, PowerModeIds.BestPerformance);
        p.Set(PowerSource.Dc, PowerModeIds.Balanced);
        Assert.Equal([(PowerSource.Ac, PowerModeIds.BestPerformance), (PowerSource.Dc, PowerModeIds.Balanced)], api.Sets);
        Assert.Equal(PowerModeIds.BestPerformance, p.Get(PowerSource.Ac));
        Assert.Equal(PowerModeIds.Balanced, p.Get(PowerSource.Dc));

        // a failing read gives null
        api.GetCode = 5;
        Assert.Null(p.Get(PowerSource.Ac));
        Assert.True(p.Supported);

        // undo goes through the same API
        api.GetCode = 0;
        var c = new PowerModeChange(p, PowerSource.Ac, PowerModeIds.BestEfficiency);
        var rec = c.Apply(Ctx());
        Assert.Equal(PowerModeIds.BestEfficiency, p.Get(PowerSource.Ac));
        c.Undo(rec, Ctx());
        Assert.Equal(PowerModeIds.BestPerformance, p.Get(PowerSource.Ac));
    }

    [Fact]
    public void Set_failure_throws_with_code()
    {
        var api = new FakeApi { SetCode = 5 };
        var p = new WinPowerModes(api.Api);
        var ex = Assert.Throws<InvalidOperationException>(() => p.Set(PowerSource.Dc, PowerModeIds.BestPerformance));
        Assert.Equal("Der Energiemodus ließ sich nicht setzen (Fehler 5).", ex.Message);
        Assert.Equal(PowerModeIds.Balanced, p.Get(PowerSource.Dc));

        // nothing is journalled as applied when the set fails
        var c = new PowerModeChange(p, PowerSource.Ac, PowerModeIds.BestEfficiency);
        Assert.Throws<InvalidOperationException>(() => c.Apply(Ctx()));
    }

    [Fact]
    public void Missing_api_marks_unsupported()
    {
        var api = new FakeApi { Missing = true };
        var p = new WinPowerModes(api.Api);
        Assert.False(p.Supported);
        Assert.Null(p.Get(PowerSource.Ac));
        var ex = Assert.Throws<InvalidOperationException>(() => p.Set(PowerSource.Ac, PowerModeIds.Balanced));
        Assert.Equal("Der Energiemodus ist auf dieser Windows-Version nicht änderbar.", ex.Message);

        // found missing on first use (Get), not only by the probe
        var api2 = new FakeApi();
        var p2 = new WinPowerModes(api2.Api);
        api2.Missing = true;
        Assert.Null(p2.Get(PowerSource.Dc));
        Assert.False(p2.Supported);
    }

    [Fact]
    public void Active_plan_smoke_read()
    {
        // read-only: the real API may or may not answer; it must never throw
        _ = new WinPowerModes().ActivePlan;
    }
}
