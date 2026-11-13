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
    readonly FakePowerPlans _plans = new();

    public PerformanceScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    PerformanceScanner Scanner(bool own = true) => new(_reg, _power, _anim, _hib, _names, own, _broadcast, _plans);

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
        Assert.StartsWith("Ausschalten gibt 13", hib.Explanation);
        Assert.EndsWith(" GB Speicherplatz auf Laufwerk C: frei. Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer. Rückgängig legt die Datei wieder an.",
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
    public void Hibernate_without_file_omits_the_size()
    {
        _hib.IsEnabled = true; _hib.FileBytes = 0;
        Assert.Equal("Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer. Rückgängig legt die Datei wieder an.",
            Row(Scanner().Scan(), "hibernate").Explanation);
    }

    [Fact]
    public void Fast_startup_needs_hiberboot_and_hibernation()
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        _hib.IsEnabled = true;
        Assert.False(Scanner().FastStartupActive()); // value missing
        _reg.Seed(Hive.LocalMachine, key, "HiberbootEnabled", 1, RegistryValueKind.DWord);
        Assert.True(Scanner().FastStartupActive());
        _hib.IsEnabled = false;
        Assert.False(Scanner().FastStartupActive());
        _hib.IsEnabled = true;
        _reg.Seed(Hive.LocalMachine, key, "HiberbootEnabled", 0, RegistryValueKind.DWord);
        Assert.False(Scanner().FastStartupActive());
        _reg.ThrowingKeys.Add(key);
        Assert.False(Scanner().FastStartupActive()); // unreadable: no note, no exception
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
    public void Background_list_hides_system_and_unresolved_packages()
    {
        const string shown = "Contoso.App_abc123";
        const string system = "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy";
        const string unresolved = "Fabrikam.Tool_def456";
        foreach (var app in new[] { shown, system, unresolved })
            _reg.SeedKey(Hive.CurrentUser, PerfValues.BackgroundRoot + "\\" + app);
        _names.Names[shown] = "Beispiel";
        _names.Names[system] = "Windows-Shell"; // resolvable, but a system package

        var rows = Scanner().Scan().Where(r => r.Group == "background").ToList();
        var row = Assert.Single(rows);
        Assert.Equal("bg:" + shown, row.Id);
        Assert.Equal("Beispiel", row.Title);
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

    [Fact]
    public void Power_rows_locked_when_plan_not_balanced()
    {
        _power.ActivePlan = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"); // "Höchstleistung"
        var sc = Scanner();
        var rows = sc.Scan();
        foreach (var id in new[] { "power-ac", "power-dc" })
        {
            var r = Row(rows, id);
            Assert.False(r.Available);
            Assert.Equal("Nur mit dem Energiesparplan „Ausgewogen“ änderbar", r.UnavailableText);
            Assert.Equal("ms-settings:powersleep", r.Link);
            Assert.Empty(sc.ChangesFor(r, 0));
        }

        _power.ActivePlan = PowerModeIds.BalancedPlan;
        Assert.True(Row(Scanner().Scan(), "power-ac").Available);
        _power.ActivePlan = null; // plan unknown: offered as before
        Assert.True(Row(Scanner().Scan(), "power-dc").Available);
    }

    [Fact]
    public void Unsupported_power_api_greys_both_power_rows()
    {
        _power.Supported = false;
        _power.Seed(PowerSource.Ac, null).Seed(PowerSource.Dc, null);
        var rows = Scanner().Scan();
        foreach (var id in new[] { "power-ac", "power-dc" })
        {
            var r = Row(rows, id);
            Assert.False(r.Available);
            Assert.Equal("Auf dieser Windows-Version nicht änderbar", r.UnavailableText);
            Assert.Empty(Scanner().ChangesFor(r, 2));
        }
    }

    [Fact]
    public void Battery_row_uses_the_journal_label()
    {
        Assert.Equal("Energiemodus (Akku)", Row(Scanner().Scan(), "power-dc").Title);
        var r2 = new PerformanceScanner(_reg, new ThrowingPower(), _anim, _hib, _names, true, _broadcast).Scan();
        Assert.Equal("Energiemodus (Akku)", Row(r2, "power-dc").Title);
    }

    sealed class ThrowingPower : IPowerModes
    {
        public bool Supported => true;
        public Guid? ActivePlan => null;
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
        PerformanceRegistration.Register(factory, _reg, _power, _anim, _hib, _broadcast, _plans);
        Dword(_reg, PerfValues.Transparency, 1);

        var sc = Scanner();
        var rows = sc.Scan();
        var changes = new[]
        {
            sc.ChangesFor(Row(rows, "transparency"), 0).Single(),
            sc.ChangesFor(Row(rows, "power-ac"), 2).Single(),
            sc.ChangesFor(Row(rows, "animation"), 0).Single(),
            sc.ChangesFor(Row(rows, "hibernate"), 0).Single(),
            sc.ChangesFor(Row(rows, "powerplan"), 1).Single(),
            sc.ChangesFor(Row(rows, "faststartup"), 1).Single(),
        };
        Assert.Equal(["perfvalue", "powermode", "spi", "hibernate", "powerplan", "perfvalue"], changes.Select(c => c.Kind));
        Assert.Contains(changes, c => c.Target.EndsWith("HiberbootEnabled"));

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
        Assert.Equal(FakePowerPlans.BalancedId, _plans.ActiveId);
        var f = PerfValues.FastStartup;
        Assert.Null(_reg.Get(f.Hive, f.Key, f.Name));
    }

    [Fact]
    public void Fast_startup_row_follows_hibernation()
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
        _hib.IsEnabled = true;
        var row = Row(Scanner().Scan(), "faststartup");
        Assert.Equal("power", row.Group);
        Assert.Equal("Schnellstart", row.Title);
        Assert.True(row.Available);
        Assert.Equal(0, row.Current); // missing = Windows default off
        Assert.Null(row.Recommended);
        Assert.Equal(["An", "Aus"], row.Options.Select(o => o.Label));
        Assert.StartsWith("Windows fährt schneller hoch", row.Explanation);

        var change = Scanner().ChangesFor(row, 1).Single();
        change.Apply(Ctx());
        Assert.Equal(1, _reg.Get(Hive.LocalMachine, key, "HiberbootEnabled"));
        Assert.Equal(1, Row(Scanner().Scan(), "faststartup").Current);

        _hib.IsEnabled = false;
        var off = Row(Scanner().Scan(), "faststartup");
        Assert.False(off.Available);
        Assert.Equal("Nur mit eingeschalteter Ruhezustand-Datei", off.UnavailableText);
        Assert.Empty(Scanner().ChangesFor(off, 0));
    }

    [Fact]
    public void Power_plan_row_lists_plans_and_changes_active()
    {
        var sc = Scanner();
        var row = Row(sc.Scan(), "powerplan");
        Assert.Equal("power", row.Group);
        Assert.Equal("Energiesparplan", row.Title);
        Assert.True(row.Available);
        Assert.Equal(["Ausgewogen", "Höchstleistung", "Energiesparmodus"], row.Options.Select(o => o.Label));
        Assert.Equal(0, row.Current);

        var change = sc.ChangesFor(row, 2).Single();
        Assert.Equal("Energiesparplan auf „Energiesparmodus“ umstellen", change.Description);
        change.Apply(Ctx());
        Assert.Equal(FakePowerPlans.SaverId, _plans.ActiveId);
        Assert.Equal(2, Row(sc.Scan(), "powerplan").Current);
        Assert.Throws<ArgumentException>(() => sc.ChangesFor(row, 7));

        _plans.ActiveId = null;
        var unreadable = Row(sc.Scan(), "powerplan");
        Assert.False(unreadable.Available);
        Assert.Equal("Nicht lesbar", unreadable.UnavailableText);
    }

    [Fact]
    public void Power_plan_change_picks_the_plan_by_id_even_after_another_scan()
    {
        // the Leistung page and the overview share one scanner: a later Scan must not shift an earlier row's choice
        var sc = Scanner();
        var row = Row(sc.Scan(), "powerplan");
        Assert.Equal("Höchstleistung", row.Options[1].Label);

        var saver = _plans.Plans.Single(p => p.Id == FakePowerPlans.SaverId);
        _plans.Plans.Remove(saver);
        _plans.Plans.Insert(0, saver);
        sc.Scan();   // e.g. the overview checked in between: the plan list now has another order

        var change = sc.ChangesFor(row, 1).Single();
        Assert.Equal("Energiesparplan auf „Höchstleistung“ umstellen", change.Description);
        change.Apply(Ctx());
        Assert.Equal(FakePowerPlans.HighId, _plans.ActiveId);
    }

    [Fact]
    public async Task Scanner_can_be_used_from_two_threads()
    {
        // unreadable sources are logged once; two threads scanning at once must not break that bookkeeping
        var sc = new PerformanceScanner(_reg, new ThrowingPower(), _anim, _hib, _names, true, _broadcast, _plans);
        var scans = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++) Assert.Contains(sc.Scan(), r => r.Id == "powerplan");
        }));
        await Task.WhenAll(scans);
    }
}
