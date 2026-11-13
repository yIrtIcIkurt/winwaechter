using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>One choice of a row. PlanId: the power plan behind a „Energiesparplan“ option (null elsewhere).</summary>
public sealed record PerfOption(int Value, string Label, Guid? PlanId = null);

public sealed record PerfSetting(string Id, string Group, string Title, string Explanation,
    IReadOnlyList<PerfOption> Options, int Current, bool Available, string? UnavailableText,
    string? Hint, int? Recommended, bool NeedsRestart, string? Link);

/// <summary>Reads the current state behind the Leistung page and turns a chosen value into changes.</summary>
public sealed class PerformanceScanner(IRegistry reg, IPowerModes power, IAnimationSetting anim, IHibernation hib,
    IAppDisplayNames names, bool animationOwnAccount, ISettingBroadcast broadcast, IPowerPlans? plans = null,
    Func<IPolicyOwnership>? policies = null)
{
    // Task 14 reads the policy owner through this; stored only for now.
    readonly Func<IPolicyOwnership>? _policies = policies;
    // The plans captured at Scan: the powerplan option values are indexes into this list.
    IReadOnlyList<PowerPlan> _planList = [];

    const string Unreadable = "Nicht lesbar";
    const string CustomLabel = "Benutzerdefiniert";
    public const string PlanLocked = "Nur mit dem Energiesparplan „Ausgewogen“ änderbar";
    const string PowerUnsupported = "Auf dieser Windows-Version nicht änderbar";
    const string BackgroundPrefix = "bg:";
    public const string BackgroundUnavailableId = "bg-unavailable";

    const string HiberbootKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    // Publisher id of Windows system and framework packages; they are not offered as background apps.
    const string SystemPublisherSuffix = "_cw5n1h2txyewy";

    const string VisualLink = SystemLinks.VisualEffects;
    const string GpuLink = SystemLinks.AdvancedGraphics;
    const string StorageLink = SystemLinks.StoragePolicies;

    static readonly PerfOption[] OnOff = [new(1, "An"), new(0, "Aus")];
    static readonly PerfOption[] GpuOptions = [new(2, "An"), new(1, "Aus")];
    static readonly PerfOption[] BackgroundOptions = [new(1, "Erlaubt"), new(0, "Nicht erlaubt")];
    static readonly PerfOption[] Frequencies =
        [new(1, "Täglich"), new(7, "Wöchentlich"), new(30, "Monatlich"), new(0, "Bei wenig Speicherplatz")];
    static readonly PerfOption[] PowerBase =
        PowerModeIds.All.Select((g, i) => new PerfOption(i, PowerModeIds.Label(g))).ToArray();

    readonly HashSet<string> _logged = [];

    public IReadOnlyList<PerfSetting> Scan()
    {
        var rows = new List<PerfSetting>();
        Source(rows, "power", ScanPower, BrokenPower);
        Source(rows, "powerplan", ScanPowerPlan, () => [Broken("powerplan", "power", "Energiesparplan", [])]);
        Source(rows, "faststartup", () => [ScanFastStartup()], () => [Broken("faststartup", "power", "Schnellstart", OnOff)]);
        Source(rows, "transparency", () => [ScanTransparency()], () => [Broken("transparency", "visual", "Transparenzeffekte", OnOff)]);
        Source(rows, "animation", () => [ScanAnimation()], () => [Broken("animation", "visual", "Animationseffekte", OnOff)]);
        Source(rows, "gamemode", () => [ScanGameMode()], () => [Broken("gamemode", "gaming", "Spielmodus", OnOff)]);
        Source(rows, "gpu", () => [ScanGpu()], () => [Broken("gpu", "gaming", "Hardwarebeschleunigte GPU-Planung", GpuOptions)]);
        Source(rows, "hibernate", ScanHibernate, () => [Broken("hibernate", "storage", "Ruhezustand-Datei", OnOff)]);
        Source(rows, "storagesense", ScanStorageSense, () =>
        [
            Broken("storagesense", "storage", "Speicheroptimierung", OnOff),
            Broken("storagesense-frequency", "storage", "Speicheroptimierung: Häufigkeit", Frequencies),
        ]);
        // When the app list itself cannot be read there are no app rows. This single row has no options and is
        // not an app: the view should render it as a note ("Nicht lesbar"), not as a checkbox.
        Source(rows, "background", ScanBackground, () =>
            [new PerfSetting(BackgroundUnavailableId, "background", "Hintergrund-Apps", "", [], 0, false, Unreadable, null, null, false, null)]);
        return rows;
    }

    // One failing source greys out only its own rows and is logged once.
    void Source(List<PerfSetting> rows, string name, Func<IEnumerable<PerfSetting>> read, Func<IEnumerable<PerfSetting>> broken)
    {
        try
        {
            rows.AddRange(read().ToList());
        }
        catch (Exception ex)
        {
            if (_logged.Add(name)) FileLog.Warn($"Leistung: Quelle „{name}“ nicht lesbar", ex);
            rows.AddRange(broken());
        }
    }

    IEnumerable<PerfSetting> BrokenPower()
    {
        yield return Broken("power-ac", "power", "Energiemodus (Netzbetrieb)", PowerBase);
        bool battery;
        try { battery = power.HasBattery; } catch { battery = true; }
        if (battery) yield return Broken("power-dc", "power", "Energiemodus (Akku)", PowerBase);
    }

    static PerfSetting Broken(string id, string group, string title, IReadOnlyList<PerfOption> options) =>
        new(id, group, title, "", options, 0, false, Unreadable, null, null, false, null);

    /// <summary>The DWORD, or the Windows default when missing. Other value types throw.</summary>
    int ReadDword(PerfValue v)
    {
        var kind = reg.GetKind(v.Hive, v.Key, v.Name);
        if (kind is null) return v.DefaultWhenMissing;
        if (kind != RegistryValueKind.DWord || reg.Get(v.Hive, v.Key, v.Name) is not { } o)
            throw new InvalidOperationException($"Der Wert „{v.Name}“ hat einen unerwarteten Typ ({kind}).");
        return Convert.ToInt32(o, CultureInfo.InvariantCulture);
    }

    // ---- power ----

    IEnumerable<PerfSetting> ScanPower()
    {
        if (!power.Supported)
        {
            // older Windows without the power mode API: shown, but neither read nor changed
            yield return Unsupported("power-ac", "Energiemodus (Netzbetrieb)");
            if (power.HasBattery) yield return Unsupported("power-dc", "Energiemodus (Akku)");
            yield break;
        }
        // Windows offers the power mode only under the plan "Ausgewogen"; under another plan a write has no effect.
        var locked = power.ActivePlan is { } plan && plan != PowerModeIds.BalancedPlan;
        yield return PowerRow(PowerSource.Ac, "power-ac", "Energiemodus (Netzbetrieb)",
            "Legt fest, wie Windows am Netzteil zwischen Leistung und Stromverbrauch abwägt.", null, locked);
        if (power.HasBattery)
            yield return PowerRow(PowerSource.Dc, "power-dc", "Energiemodus (Akku)",
                "Legt fest, wie Windows im Akkubetrieb zwischen Leistung und Laufzeit abwägt.",
                "Beste Leistung im Akkubetrieb leert den Akku spürbar schneller.", locked);
    }

    static PerfSetting Unsupported(string id, string title) =>
        new(id, "power", title, "", PowerBase, 0, false, PowerUnsupported, null, null, false, SystemLinks.PowerSleep);

    PerfSetting PowerRow(PowerSource s, string id, string title, string explanation, string? hint, bool locked)
    {
        var mode = power.Get(s);
        if (mode is null)
            return new(id, "power", title, explanation, PowerBase, 0, false, Unreadable, hint, null, false, null);
        var index = PowerModeIds.All.ToList().IndexOf(mode.Value);
        IReadOnlyList<PerfOption> options = index < 0 ? [new PerfOption(-1, CustomLabel), .. PowerBase] : PowerBase;
        return locked
            ? new(id, "power", title, explanation, options, index, false, PlanLocked, hint, null, false, SystemLinks.PowerSleep)
            : new(id, "power", title, explanation, options, index, true, null, hint, null, false, SystemLinks.PowerSleep);
    }

    IEnumerable<PerfSetting> ScanPowerPlan()
    {
        _planList = [];
        if (plans is null) yield break; // not wired: no row
        const string explanation = "Der Energiesparplan bestimmt, wie Windows Strom und Leistung grundsätzlich abwägt.";
        var list = plans.List();
        var active = plans.Active();
        _planList = list;
        if (list.Count == 0 || active is null)
        {
            yield return new("powerplan", "power", "Energiesparplan", explanation, [], 0, false, Unreadable,
                null, null, false, SystemLinks.PowerSleep);
            yield break;
        }
        var options = list.Select((p, i) => new PerfOption(i, p.Name, p.Id)).ToList();
        var index = list.ToList().FindIndex(p => p.Id == active.Value);
        if (index < 0) options.Insert(0, new PerfOption(-1, CustomLabel));
        yield return new("powerplan", "power", "Energiesparplan", explanation, options, index, true, null,
            null, null, false, SystemLinks.PowerSleep);
    }

    PerfSetting ScanFastStartup()
    {
        const string explanation = "Windows fährt schneller hoch, weil es beim Herunterfahren einen Teil des Systems sichert. "
            + "Dafür wirken manche Updates und Treiberwechsel erst nach „Neu starten“, und das Gerät ist nie ganz aus.";
        var on = ReadDword(PerfValues.FastStartup) == 1 ? 1 : 0;
        var hibOn = hib.IsEnabled;
        return new("faststartup", "power", "Schnellstart", explanation, OnOff, on, hibOn,
            hibOn ? null : "Nur mit eingeschalteter Ruhezustand-Datei", null, null, false, null);
    }

    // ---- visual ----

    PerfSetting ScanTransparency() => new("transparency", "visual", "Transparenzeffekte",
        "Durchscheinende Flächen in Startmenü, Taskleiste und Fenstern. Ausschalten spart etwas Grafikleistung.",
        OnOff, ReadDword(PerfValues.Transparency) == 0 ? 0 : 1, true, null, null, null, false, VisualLink);

    PerfSetting ScanAnimation() => new("animation", "visual", "Animationseffekte",
        "Bewegte Übergänge beim Öffnen, Schließen und Wechseln von Fenstern.",
        OnOff, animationOwnAccount && anim.Get() ? 1 : 0, animationOwnAccount, // not read for another account
        animationOwnAccount ? null : "Nur im eigenen Benutzerkonto änderbar", null, null, false, VisualLink);

    // ---- gaming ----

    PerfSetting ScanGameMode() => new("gamemode", "gaming", "Spielmodus",
        "Windows gibt Spielen Vorrang bei Prozessor und Grafikkarte und hält Hintergrundaufgaben zurück.",
        OnOff, ReadDword(PerfValues.GameMode) == 0 ? 0 : 1, true, null, null, null, false, null);

    PerfSetting ScanGpu()
    {
        const string title = "Hardwarebeschleunigte GPU-Planung";
        const string explanation = "Die Grafikkarte plant ihre Aufgaben selbst, statt den Prozessor zu belasten. Gilt nach einem Neustart.";
        var v = ReadDword(PerfValues.GpuScheduling);
        if (v is 1 or 2)
            return new("gpu", "gaming", title, explanation, GpuOptions, v, true, null, null, null, true, GpuLink);
        return new("gpu", "gaming", title, explanation, GpuOptions, 0, false,
            "Wird von Windows für Ihre Grafikkarte automatisch geregelt", null, null, true, GpuLink);
    }

    // ---- storage ----

    IEnumerable<PerfSetting> ScanHibernate()
    {
        var enabled = hib.IsEnabled;
        var bytes = hib.FileBytes;
        if (!enabled && bytes <= 0) yield break;
        // hiberfil.sys lives on the system drive: say so, it is disk space, not memory
        var frees = bytes > 0 ? $"Ausschalten gibt {SizeText.Format(bytes)} Speicherplatz auf Laufwerk {hib.Drive} frei. " : "";
        yield return new("hibernate", "storage", "Ruhezustand-Datei",
            frees + "Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer. "
                + "Rückgängig legt die Datei wieder an.",
            OnOff, enabled ? 1 : 0, true, null,
            power.HasBattery ? "Bei leerem Akku sichert Windows Ihre Arbeit sonst über den Ruhezustand." : null,
            null, false, null);
    }

    IEnumerable<PerfSetting> ScanStorageSense()
    {
        var on = ReadDword(PerfValues.StorageSenseOn) == 1 ? 1 : 0;
        yield return new("storagesense", "storage", "Speicheroptimierung",
            "Windows räumt temporäre Dateien und den Papierkorb automatisch auf.",
            OnOff, on, true, null, null, 1, false, StorageLink);

        var freq = ReadDword(PerfValues.StorageSenseFrequency);
        var known = Frequencies.Any(o => o.Value == freq);
        IReadOnlyList<PerfOption> options = known ? Frequencies : [new PerfOption(-1, CustomLabel), .. Frequencies];
        yield return new("storagesense-frequency", "storage", "Speicheroptimierung: Häufigkeit",
            "Wie oft Windows automatisch aufräumt.",
            options, known ? freq : -1, on == 1, on == 1 ? null : "Nur bei eingeschalteter Speicheroptimierung",
            null, null, false, null);
    }

    // ---- background apps ----

    IEnumerable<PerfSetting> ScanBackground()
    {
        // System/framework packages and apps without a resolvable name are left out: the user would not know them.
        var apps = reg.SubKeyNames(Hive.CurrentUser, PerfValues.BackgroundRoot)
            .Where(PerfValues.IsBackgroundApp)
            .Where(a => !a.EndsWith(SystemPublisherSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(a => (App: a, Title: SafeName(a)))
            .Where(a => a.Title is not null)
            .Select(a => (a.App, Title: a.Title!))
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var (app, title) in apps)
        {
            PerfSetting row;
            try
            {
                row = new(BackgroundPrefix + app, "background", title,
                    "Darf als Store-App im Hintergrund laufen, auch wenn sie nicht geöffnet ist.",
                    BackgroundOptions, ReadDword(PerfValues.BackgroundDisabled(app)) == 1 ? 0 : 1, true, null,
                    null, null, false, null);
            }
            catch (Exception ex)
            {
                if (_logged.Add("bg:" + app)) FileLog.Warn($"Leistung: Hintergrund-App „{app}“ nicht lesbar", ex);
                row = new(BackgroundPrefix + app, "background", title, "", BackgroundOptions, 0, false, Unreadable,
                    null, null, false, null);
            }
            yield return row;
        }
    }

    string? SafeName(string app)
    {
        try
        {
            return names.TryResolve(app, out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;
        }
        catch (Exception ex)
        {
            if (_logged.Add("name:" + app)) FileLog.Warn($"Leistung: Anzeigename von „{app}“ nicht lesbar", ex);
            return null;
        }
    }

    // ---- fast startup ----

    /// <summary>True when Windows shuts down with fast startup (HiberbootEnabled = 1 and hibernation on);
    /// false when that cannot be read.</summary>
    public bool FastStartupActive()
    {
        try
        {
            return reg.Get(Hive.LocalMachine, HiberbootKey, "HiberbootEnabled") is int v && v == 1 && hib.IsEnabled;
        }
        catch (Exception ex)
        {
            if (_logged.Add("faststartup")) FileLog.Warn("Leistung: Schnellstart-Zustand nicht lesbar", ex);
            return false;
        }
    }

    // ---- changes ----

    /// <summary>The changes that bring the setting from its current value to the chosen one; empty when nothing differs.</summary>
    public IReadOnlyList<IChange> ChangesFor(PerfSetting s, int chosen)
    {
        if (!s.Available || chosen == s.Current) return [];
        if (chosen == -1)
            throw new ArgumentException("„Benutzerdefiniert“ kann nicht gewählt werden.", nameof(chosen));
        if (s.Options.All(o => o.Value != chosen))
            throw new ArgumentException($"Der Wert {chosen} gehört nicht zu „{s.Title}“.", nameof(chosen));

        var on = chosen != 0;
        switch (s.Id)
        {
            case "power-ac": return [new PowerModeChange(power, PowerSource.Ac, PowerModeIds.All[chosen])];
            case "power-dc": return [new PowerModeChange(power, PowerSource.Dc, PowerModeIds.All[chosen])];
            case "powerplan":
                return plans is not null && chosen >= 0 && chosen < _planList.Count
                    ? [new PowerPlanChange(plans, _planList[chosen].Id)]
                    : throw new ArgumentException($"Der Wert {chosen} gehört nicht zu „{s.Title}“.", nameof(chosen));
            case "faststartup":
                return [new PerfValueChange(reg, PerfValues.FastStartup, chosen,
                    on ? "Schnellstart einschalten" : "Schnellstart ausschalten", null)];
            case "transparency":
                return [new PerfValueChange(reg, PerfValues.Transparency, chosen,
                    on ? "Transparenzeffekte einschalten" : "Transparenzeffekte ausschalten", broadcast)];
            case "animation": return [new AnimationChange(anim, on)];
            case "gamemode":
                return [new PerfValueChange(reg, PerfValues.GameMode, chosen,
                    on ? "Spielmodus einschalten" : "Spielmodus ausschalten", null)];
            case "gpu":
                return [new PerfValueChange(reg, PerfValues.GpuScheduling, chosen,
                    chosen == 2 ? "Hardwarebeschleunigte GPU-Planung einschalten" : "Hardwarebeschleunigte GPU-Planung ausschalten", null)];
            case "hibernate": return [new HibernateChange(hib, on)];
            case "storagesense":
                return [new PerfValueChange(reg, PerfValues.StorageSenseOn, chosen,
                    on ? "Speicheroptimierung einschalten" : "Speicheroptimierung ausschalten", null)];
            case "storagesense-frequency":
                return [new PerfValueChange(reg, PerfValues.StorageSenseFrequency, chosen,
                    $"Speicheroptimierung: {Frequencies.First(o => o.Value == chosen).Label}", null)];
        }

        if (s.Id.StartsWith(BackgroundPrefix, StringComparison.Ordinal))
        {
            var app = s.Id[BackgroundPrefix.Length..];
            var disabled = on ? 0 : 1;
            var what = on ? "darf im Hintergrund laufen" : "im Hintergrund sperren";
            var list = new List<IChange>
            {
                new PerfValueChange(reg, PerfValues.BackgroundDisabled(app), disabled, $"{s.Title}: {what}", null),
            };
            var byUser = PerfValues.BackgroundDisabledByUser(app);
            if (ReadDword(byUser) != disabled)
                list.Add(new PerfValueChange(reg, byUser, disabled, $"{s.Title}: {what} (Benutzerwahl)", null));
            return list;
        }
        throw new ArgumentException($"Unbekannte Einstellung „{s.Id}“.", nameof(s));
    }
}
