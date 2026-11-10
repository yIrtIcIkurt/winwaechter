using Microsoft.Win32;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PerfValueTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-perf-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeBroadcast _bc = new();

    public PerfValueTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    PerfValueChange Change(PerfValue v, int target) => new(_reg, v, target, "Test", _bc);

    [Fact]
    public void Apply_and_undo_restore_exactly()
    {
        var v = PerfValues.GameMode;
        _reg.Seed(v.Hive, v.Key, v.Name, 1, RegistryValueKind.DWord);
        var c = Change(v, 0);
        var rec = c.Apply(Ctx());
        Assert.Equal("perfvalue", rec.Kind);
        Assert.Equal(0, _reg.Get(v.Hive, v.Key, v.Name));
        c.Undo(rec, Ctx());
        Assert.Equal(1, _reg.Get(v.Hive, v.Key, v.Name));
        Assert.Equal(RegistryValueKind.DWord, _reg.GetKind(v.Hive, v.Key, v.Name));

        // missing value: key created on apply, value deleted on undo
        var s = PerfValues.StorageSenseOn;
        Assert.False(_reg.KeyExists(s.Hive, s.Key));
        var c2 = Change(s, 1);
        var rec2 = c2.Apply(Ctx());
        Assert.True(_reg.KeyExists(s.Hive, s.Key));
        Assert.Equal(1, _reg.Get(s.Hive, s.Key, s.Name));
        c2.Undo(rec2, Ctx());
        Assert.Null(_reg.GetKind(s.Hive, s.Key, s.Name));
    }

    [Fact]
    public void Missing_or_odd_values_are_handled()
    {
        var v = PerfValues.Transparency;
        _reg.Seed(v.Hive, v.Key, v.Name, new byte[] { 1 }, RegistryValueKind.Binary);
        var ex = Assert.Throws<InvalidOperationException>(() => Change(v, 0).Apply(Ctx()));
        Assert.Equal("Der Wert „EnableTransparency“ hat einen unerwarteten Typ (Binary) und wird nicht verändert.", ex.Message);
        Assert.Empty(_reg.SetCalls);

        var g = PerfValues.GameMode;
        Assert.False(_reg.KeyExists(g.Hive, g.Key));
        var rec = Change(g, 0).Apply(Ctx());
        Assert.Equal("false", rec.Before["existed"]);
        Assert.Equal(0, _reg.Get(g.Hive, g.Key, g.Name));
    }

    [Fact]
    public void Drift_is_detected()
    {
        var v = PerfValues.GameMode;
        _reg.Seed(v.Hive, v.Key, v.Name, 1, RegistryValueKind.DWord);
        var c = Change(v, 0);
        var rec = c.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        _reg.Seed(v.Hive, v.Key, v.Name, 1, RegistryValueKind.DWord);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
    }

    [Fact]
    public void Transparency_broadcasts_on_apply_and_undo()
    {
        var v = PerfValues.Transparency;
        _reg.Seed(v.Hive, v.Key, v.Name, 1, RegistryValueKind.DWord);
        var c = Change(v, 0);
        var rec = c.Apply(Ctx());
        c.Undo(rec, Ctx());
        Assert.Equal(2, _bc.Count);

        var g = PerfValues.GameMode;
        var c2 = Change(g, 0);
        c2.Undo(c2.Apply(Ctx()), Ctx());
        Assert.Equal(2, _bc.Count);
    }

    static ChangeRecord Rec(string hive, string key, string name, string? target) =>
        new("id", "perfvalue", "x", "x",
            new() { ["hive"] = hive, ["key"] = key, ["name"] = name, ["existed"] = "false" },
            new() { ["value"] = target, ["kind"] = "DWord" },
            ChangeStatus.Applied, DateTimeOffset.Now);

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        var t = PerfValues.Transparency;
        Assert.NotNull(PerfValueChange.FromRecord(Rec("HKCU", t.Key, t.Name, "0"), _reg, _bc));
        Assert.NotNull(PerfValueChange.FromRecord(Rec("HKCU", PerfValues.BackgroundRoot + @"\app", "Disabled", "1"), _reg, _bc));

        void Rejects(ChangeRecord r) =>
            Assert.Equal("Dieser Wert gehört nicht zu den Leistungs-Einstellungen – Rückgängig abgelehnt.",
                Assert.Throws<InvalidOperationException>(() => PerfValueChange.FromRecord(r, _reg, _bc)).Message);

        Rejects(Rec("HKCU", @"Software\Other", "X", "0"));
        Rejects(Rec("HKCU", PerfValues.BackgroundRoot + @"\a\b", "Disabled", "1"));
        Rejects(Rec("HKCU", PerfValues.BackgroundRoot + @"\..", "Disabled", "1"));
        Rejects(Rec("HKCU", t.Key, t.Name, "5"));
        Rejects(Rec("HKCU", t.Key, t.Name, null));
        Rejects(Rec("HKCU", t.Key, t.Name, "abc"));
        Rejects(Rec("HKLM", t.Key, t.Name, "0"));
        Rejects(Rec("HKXX", t.Key, t.Name, "0"));
    }

    [Fact]
    public void Target_outside_list_throws()
    {
        Assert.Throws<ArgumentException>(() => Change(PerfValues.Transparency, 5));
        Assert.Throws<ArgumentException>(() => Change(PerfValues.GpuScheduling, 0));
        Assert.Throws<ArgumentException>(() => Change(PerfValues.StorageSenseFrequency, 14));
        Change(PerfValues.StorageSenseFrequency, 30);
    }

    [Fact]
    public void Background_app_values_accepted_only_for_direct_children()
    {
        Assert.True(PerfValues.IsBackgroundApp("Microsoft.Photos_8wekyb3d8bbwe"));
        foreach (var bad in new[] { "", "a\b", "a/b", "..", "a..b", "a\u0001b", new string('a', 201) })
            Assert.False(PerfValues.IsBackgroundApp(bad), bad);

        var d = PerfValues.BackgroundDisabled("App1");
        Assert.Equal(PerfValues.BackgroundRoot + @"\App1", d.Key);
        Assert.Equal("Disabled", d.Name);
        Assert.Equal("DisabledByUser", PerfValues.BackgroundDisabledByUser("App1").Name);
        Assert.Throws<ArgumentException>(() => PerfValues.BackgroundDisabled(@"..\x"));

        Assert.NotNull(PerfValues.Find(Hive.CurrentUser, PerfValues.BackgroundRoot + @"\App1", "DisabledByUser"));
        Assert.Null(PerfValues.Find(Hive.CurrentUser, PerfValues.BackgroundRoot + @"\App1", "Other"));
        Assert.Null(PerfValues.Find(Hive.LocalMachine, PerfValues.BackgroundRoot + @"\App1", "Disabled"));
        Assert.Null(PerfValues.Find(Hive.CurrentUser, PerfValues.BackgroundRoot, "Disabled"));
    }

    [Fact]
    public void Whitelist_matches_spec()
    {
        Assert.Equal(Hive.LocalMachine, PerfValues.GpuScheduling.Hive);
        Assert.Equal(new[] { 1, 2 }, PerfValues.GpuScheduling.Targets);
        Assert.Equal(new[] { 0, 1, 7, 30 }, PerfValues.StorageSenseFrequency.Targets);
        Assert.Equal("2048", PerfValues.StorageSenseFrequency.Name);
        Assert.Equal("01", PerfValues.StorageSenseOn.Name);
        Assert.True(PerfValues.Transparency.Broadcast);
        Assert.False(PerfValues.GameMode.Broadcast);
        Assert.Same(PerfValues.GameMode, PerfValues.Find(Hive.CurrentUser, PerfValues.GameMode.Key, "AutoGameModeEnabled"));
    }
}
