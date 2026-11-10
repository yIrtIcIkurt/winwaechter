using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PerformanceScannerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-perfscan-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakePowerModes _power = new();
    readonly FakeAnimationSetting _anim = new();
    readonly FakeHibernation _hib = new();
    readonly FakeAppDisplayNames _names = new();
    readonly FakeBroadcast _broadcast = new();

    public PerformanceScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    PerformanceScanner Scanner(bool own = true) => new(_reg, _power, _anim, _hib, _names, own, _broadcast);

    static PerfSetting Row(IReadOnlyList<PerfSetting> rows, string id) => rows.Single(r => r.Id == id);

    static void Dword(FakeRegistry reg, PerfValue v, int value) =>
        reg.Seed(v.Hive, v.Key, v.Name, value, RegistryValueKind.DWord);

    [Fact]
    public void Scan_reads_current_values_and_defaults()
    {
        Dword(_reg, PerfValues.Transparency, 0);
        _power.Seed(PowerSource.Ac, PowerModeIds.BestPerformance).Seed(PowerSource.Dc, PowerModeIds.BestEfficiency);
        _anim.Value = false;
        // GameBar key missing entirely -> Windows default "An".
        var rows = Scanner().Scan();

        Assert.Equal(0, Row(rows, "transparency").Current);
        Assert.Equal(1, Row(rows, "gamemode").Current);
        Assert.Equal(2, Row(rows, "power-ac").Current);
        Assert.Equal(0, Row(rows, "power-dc").Current);
        Assert.Equal(0, Row(rows, "animation").Current);
        Assert.Equal("power", Row(rows, "power-ac").Group);
        Assert.Equal("visual", Row(rows, "transparency").Group);
        Assert.Equal("ms-settings:easeofaccess-visualeffects", Row(rows, "transparency").Link);
        Assert.Equal(["Beste Energieeffizienz", "Ausgewogen", "Beste Leistung"],
            Row(rows, "power-ac").Options.Select(o => o.Label));

        var hib = Row(rows, "hibernate");
        Assert.Equal(1, hib.Current);
        Assert.StartsWith("Gibt 13", hib.Explanation);
        Assert.EndsWith(" GB frei. Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer.",
            hib.Explanation);
        Assert.Equal("Bei leerem Akku sichert Windows Ihre Arbeit sonst über den Ruhezustand.", hib.Hint);

        var ss = Row(rows, "storagesense");
        Assert.Equal(0, ss.Current);
        Assert.Equal(1, ss.Recommended);
        Assert.Equal("ms-settings:storagepolicies", ss.Link);
    }

    [Fact]
    public void Unexpected_value_type_greys_only_that_row()
    {
        var t = PerfValues.Transparency;
        _reg.Seed(t.Hive, t.Key, t.Name, "1");
        var rows = Scanner().Scan();
        Assert.False(Row(rows, "transparency").Available);
        Assert.Equal("Nicht lesbar", Row(rows, "transparency").UnavailableText);
        Assert.True(Row(rows, "gamemode").Available);
    }

    [Fact]
    public void No_battery_hides_power_dc_and_hint()
    {
        _power.HasBattery = false;
        var rows = Scanner().Scan();
        Assert.DoesNotContain(rows, r => r.Id == "power-dc");
        Assert.Null(Row(rows, "hibernate").Hint);

        _power.HasBattery = true;
        var dc = Row(Scanner().Scan(), "power-dc");
        Assert.Equal("Beste Leistung im Akkubetrieb leert den Akku spürbar schneller.", dc.Hint);
    }

    [Fact]
    public void Hibernate_only_offered_when_enabled_or_file_exists()
    {
        _hib.IsEnabled = false; _hib.FileBytes = 0;
        Assert.DoesNotContain(Scanner().Scan(), r => r.Id == "hibernate");
        _hib.FileBytes = 1024L * 1024 * 1024;
        Assert.Equal(0, Row(Scanner().Scan(), "hibernate").Current);
    }

    [Fact]
    public void Gpu_without_value_is_info_only()
    {
        var gpu = Row(Scanner().Scan(), "gpu");
        Assert.False(gpu.Available);
        Assert.Equal("Wird von Windows für Ihre Grafikkarte automatisch geregelt", gpu.UnavailableText);
        Assert.Equal("ms-settings:display-advancedgraphics", gpu.Link);
        Assert.True(gpu.NeedsRestart);

        Dword(_reg, PerfValues.GpuScheduling, 2);
        gpu = Row(Scanner().Scan(), "gpu");
        Assert.True(gpu.Available);
        Assert.Equal(2, gpu.Current);
    }

    [Fact]
    public void Animation_not_own_account_is_greyed()
    {
        var a = Row(Scanner(own: false).Scan(), "animation");
        Assert.False(a.Available);
        Assert.Equal("Nur im eigenen Benutzerkonto änderbar", a.UnavailableText);
    }

    [Fact]
    public void Frequency_only_available_when_storage_sense_on()
    {
        Dword(_reg, PerfValues.StorageSenseOn, 0);
        Dword(_reg, PerfValues.StorageSenseFrequency, 7);
        var off = Row(Scanner().Scan(), "storagesense-frequency");
        Assert.False(off.Available);
        Assert.Equal(7, off.Current);

        Dword(_reg, PerfValues.StorageSenseOn, 1);
        var on = Row(Scanner().Scan(), "storagesense-frequency");
        Assert.True(on.Available);
        Assert.Equal([1, 7, 30, 0], on.Options.Select(o => o.Value));
        Assert.Equal(["Täglich", "Wöchentlich", "Monatlich", "Bei wenig Speicherplatz"], on.Options.Select(o => o.Label));
    }

    [Fact]
    public void ChangesFor_returns_only_differences()
    {
        Dword(_reg, PerfValues.StorageSenseOn, 1);
        _power.Seed(PowerSource.Ac, PowerModeIds.BestPerformance);
        var sc = Scanner();
        var rows = sc.Scan();

        var ss = Row(rows, "storagesense");
        Assert.Empty(sc.ChangesFor(ss, 1));
        var c = Assert.Single(sc.ChangesFor(ss, 0));
        Assert.Equal("perfvalue", c.Kind);
        c.Apply(Ctx());
        var on = PerfValues.StorageSenseOn;
        Assert.Equal(0, _reg.Get(on.Hive, on.Key, on.Name));

        var ac = Row(rows, "power-ac");
        var pc = Assert.Single(sc.ChangesFor(ac, 0));
        Assert.Equal("powermode", pc.Kind);
        pc.Apply(Ctx());
        Assert.Equal((PowerSource.Ac, PowerModeIds.BestEfficiency), _power.SetCalls.Single());

        // Not available: never a change.
        Assert.Empty(sc.ChangesFor(Row(rows, "gpu"), 2));
    }

    [Fact]
    public void Custom_power_option_cannot_be_chosen()
    {
        _power.Seed(PowerSource.Ac, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var sc = Scanner();
        var ac = Row(sc.Scan(), "power-ac");
        Assert.Equal(-1, ac.Current);
        Assert.Contains(ac.Options, o => o.Value == -1 && o.Label == "Benutzerdefiniert");
        Assert.Empty(sc.ChangesFor(ac, -1)); // unchanged selection
        Assert.Throws<ArgumentException>(() => sc.ChangesFor(ac, 7));
        Assert.Throws<ArgumentException>(() => sc.ChangesFor(ac with { Current = 1 }, -1));
        Assert.Single(sc.ChangesFor(ac, 1));
    }

    [Fact]
    public void Background_apps_listed_with_names_and_state()
    {
        const string a = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
        const string b = "Contoso.App_abc123";
        Dword(_reg, PerfValues.BackgroundDisabled(a), 1);
        Dword(_reg, PerfValues.BackgroundDisabledByUser(a), 1);
        _reg.SeedKey(Hive.CurrentUser, PerfValues.BackgroundRoot + "\\" + b);
        _names.Names[a] = "Rechner";
        _names.Names[b] = "Beispiel";

        var sc = Scanner();
        var rows = sc.Scan().Where(r => r.Group == "background").ToList();
        Assert.Equal(["Beispiel", "Rechner"], rows.Select(r => r.Title));
        var calc = Row(rows, "bg:" + a);
        Assert.Equal(0, calc.Current);
        Assert.Equal(["Erlaubt", "Nicht erlaubt"], calc.Options.Select(o => o.Label));
        Assert.Equal(1, Row(rows, "bg:" + b).Current);

        // Allow again: both values go back; block: Disabled and DisabledByUser set.
        Assert.Equal(2, sc.ChangesFor(calc, 1).Count);
        Assert.Equal(2, sc.ChangesFor(Row(rows, "bg:" + b), 0).Count);
    }

    [Fact]
    public void Failing_source_greys_only_its_rows()
    {
        var gm = PerfValues.GameMode;
        Dword(_reg, gm, 1);
        _reg.ThrowingKeys.Add(gm.Key);
        var sc = Scanner();
        var rows = sc.Scan();
        var bad = Row(rows, "gamemode");
        Assert.False(bad.Available);
        Assert.Equal("Nicht lesbar", bad.UnavailableText);
        Assert.True(Row(rows, "transparency").Available);
        Assert.True(Row(rows, "power-ac").Available);
        Assert.Empty(sc.ChangesFor(bad, 0));
        // A second scan must not throw either.
        Assert.False(Row(sc.Scan(), "gamemode").Available);
    }

    [Fact]
    public void Failed_power_and_storage_sources_grey_all_their_rows()
    {
        var p = PerfValues.StorageSenseOn;
        Dword(_reg, p, 1);
        _reg.ThrowingKeys.Add(p.Key);
        var rows = Scanner().Scan();
        foreach (var id in new[] { "storagesense", "storagesense-frequency" })
        {
            Assert.False(Row(rows, id).Available);
            Assert.Equal("Nicht lesbar", Row(rows, id).UnavailableText);
        }

        var r2 = new PerformanceScanner(_reg, new ThrowingPower(), _anim, _hib, _names, true, _broadcast).Scan();
        Assert.False(Row(r2, "power-ac").Available);
        Assert.False(Row(r2, "power-dc").Available);
    }

    sealed class ThrowingPower : IPowerModes
    {
        public bool HasBattery => throw new InvalidOperationException("x");
        public PowerSource Current => PowerSource.Ac;
        public Guid? Get(PowerSource s) => throw new InvalidOperationException("x");
        public void Set(PowerSource s, Guid g) { }
    }

    [Fact]
    public void Unreadable_background_list_gives_note_row_without_options()
    {
        _reg.ThrowingKeys.Add(PerfValues.BackgroundRoot);
        var row = Row(Scanner().Scan(), PerformanceScanner.BackgroundUnavailableId);
        Assert.Equal("background", row.Group);
        Assert.Empty(row.Options);
        Assert.False(row.Available);
    }

    [Fact]
    public void Animation_not_own_account_is_not_read()
    {
        var sc = new PerformanceScanner(_reg, _power, new ThrowingAnim(), _hib, _names, false, _broadcast);
        var a = Row(sc.Scan(), "animation");
        Assert.False(a.Available);
        Assert.Equal("Nur im eigenen Benutzerkonto änderbar", a.UnavailableText);
    }

    sealed class ThrowingAnim : IAnimationSetting
    {
        public bool Get() => throw new InvalidOperationException("x");
        public void Set(bool on) { }
    }

    [Fact]
    public void Registered_kinds_rehydrate()
    {
        var factory = new ChangeFactory();
        PerformanceRegistration.Register(factory, _reg, _power, _anim, _hib, _broadcast);
        Dword(_reg, PerfValues.Transparency, 1);

        var sc = Scanner();
        var rows = sc.Scan();
        var changes = new[]
        {
            sc.ChangesFor(Row(rows, "transparency"), 0).Single(),
            sc.ChangesFor(Row(rows, "power-ac"), 2).Single(),
            sc.ChangesFor(Row(rows, "animation"), 0).Single(),
            sc.ChangesFor(Row(rows, "hibernate"), 0).Single(),
        };
        Assert.Equal(["perfvalue", "powermode", "spi", "hibernate"], changes.Select(c => c.Kind));

        foreach (var change in changes)
        {
            var record = change.Apply(Ctx());
            var again = factory.Rehydrate(record);
            Assert.Equal(record.Kind, again.Kind);
            again.Undo(record, Ctx());
        }

        var t = PerfValues.Transparency;
        Assert.Equal(1, _reg.Get(t.Hive, t.Key, t.Name));
        Assert.Equal(PowerModeIds.Balanced, _power.Get(PowerSource.Ac));
        Assert.True(_anim.Value);
        Assert.True(_hib.IsEnabled);
    }
}
