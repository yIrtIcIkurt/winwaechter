using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Performance;

public enum PowerSource { Ac, Dc }

/// <summary>The three Windows power modes (overlay schemes) the Leistung page may switch to.</summary>
public static class PowerModeIds
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static readonly IReadOnlyList<Guid> All = [BestEfficiency, Balanced, BestPerformance];

    /// <summary>The power plan "Ausgewogen"; Windows offers the power modes only while it is active.</summary>
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static string Label(Guid mode) =>
        mode == BestEfficiency ? "Beste Energieeffizienz"
        : mode == Balanced ? "Ausgewogen"
        : mode == BestPerformance ? "Beste Leistung"
        : "Benutzerdefiniert";
}

public interface IPowerModes
{
    bool HasBattery { get; }
    PowerSource Current { get; }

    /// <summary>The active power plan; null when it cannot be read.</summary>
    Guid? ActivePlan { get; }

    /// <summary>The active mode for this source; null when the stored value cannot be read as a GUID.</summary>
    Guid? Get(PowerSource s);

    void Set(PowerSource s, Guid mode);
}

/// <param name="setOverlay">Sets the active overlay scheme and returns the Win32 result (0 = success);
/// defaults to <c>PowerSetActiveOverlayScheme</c>. Tests inject a fake.</param>
public sealed class WinPowerModes(IRegistry reg, Func<Guid, uint>? setOverlay = null) : IPowerModes
{
    const string Key = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    const uint ErrorModNotFound = 126, ErrorProcNotFound = 127;

    readonly Func<Guid, uint> _setOverlay = setOverlay ?? PowerNative.PowerSetActiveOverlayScheme;

    static string ValueName(PowerSource s) =>
        s == PowerSource.Ac ? "ActiveOverlayAcPowerScheme" : "ActiveOverlayDcPowerScheme";

    public bool HasBattery => !(Status() is { } st && (st.BatteryFlag is 128 or 255));

    public PowerSource Current => Status() is { ACLineStatus: 0 } ? PowerSource.Dc : PowerSource.Ac;

    public Guid? ActivePlan
    {
        get
        {
            try
            {
                if (PowerNative.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
                try { return Marshal.PtrToStructure<Guid>(ptr); }
                finally { PowerNative.LocalFree(ptr); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
    }

    public Guid? Get(PowerSource s) => reg.Get(Hive.LocalMachine, Key, ValueName(s)) switch
    {
        null => PowerModeIds.Balanced,
        string text when Guid.TryParse(text, out var g) => g,
        _ => null,
    };

    /// <summary>Writes the mode; for the active power source it also applies it at once. When that fails the old
    /// registry value is restored and the call throws, so nothing is journalled as applied without effect.</summary>
    public void Set(PowerSource s, Guid mode)
    {
        var name = ValueName(s);
        var oldKind = reg.GetKind(Hive.LocalMachine, Key, name);
        var old = oldKind is null ? null : reg.Get(Hive.LocalMachine, Key, name);
        reg.Set(Hive.LocalMachine, Key, name, mode.ToString("D"), RegistryValueKind.String);
        if (s != Current) return; // applies at the next power-source change

        uint code;
        try
        {
            code = _setOverlay(mode);
        }
        catch (DllNotFoundException) { code = ErrorModNotFound; }
        catch (EntryPointNotFoundException) { code = ErrorProcNotFound; }
        if (code == 0) return;

        if (oldKind is { } k && old is not null) reg.Set(Hive.LocalMachine, Key, name, old, k);
        else reg.Delete(Hive.LocalMachine, Key, name);
        throw new InvalidOperationException($"Der Energiemodus ließ sich nicht setzen (Fehler {code}).");
    }

    static PowerNative.SystemPowerStatus? Status()
    {
        try { return PowerNative.GetSystemPowerStatus(out var st) ? st : null; }
        catch { return null; }
    }
}

/// <summary>Switches the Windows power mode for mains or battery operation to one of the three fixed modes.</summary>
public sealed class PowerModeChange : IChange
{
    readonly IPowerModes _modes;
    readonly PowerSource _source;
    readonly Guid _target;

    public PowerModeChange(IPowerModes modes, PowerSource source, Guid target)
    {
        if (!PowerModeIds.All.Contains(target))
            throw new ArgumentException("Dieser Energiemodus ist nicht erlaubt.", nameof(target));
        _modes = modes; _source = source; _target = target;
    }

    public string Kind => "powermode";
    public string Target => SourceLabel(_source);
    public string Description => $"{Target} auf „{PowerModeIds.Label(_target)}“";

    static string SourceLabel(PowerSource s) =>
        s == PowerSource.Ac ? "Energiemodus (Netzbetrieb)" : "Energiemodus (Akku)";

    static string SourceId(PowerSource s) => s == PowerSource.Ac ? "ac" : "dc";

    public static PowerModeChange FromRecord(ChangeRecord r, IPowerModes modes)
    {
        var source = r.Before.GetValueOrDefault("source") switch
        {
            "ac" => PowerSource.Ac,
            "dc" => PowerSource.Dc,
            _ => throw NotListed(),
        };
        if (!Guid.TryParse(r.After.GetValueOrDefault("mode"), out var target) || !PowerModeIds.All.Contains(target))
            throw NotListed();
        return new PowerModeChange(modes, source, target);
    }

    static InvalidOperationException NotListed() =>
        new("Dieser Energiemodus gehört nicht zu den Leistungs-Einstellungen – Rückgängig abgelehnt.");

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = new Dictionary<string, string?>
        {
            ["source"] = SourceId(_source),
            ["mode"] = _modes.Get(_source)?.ToString("D") ?? "",
        };
        _modes.Set(_source, _target);
        var after = new Dictionary<string, string?> { ["mode"] = _target.ToString("D") };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (!Guid.TryParse(record.Before.GetValueOrDefault("mode"), out var old))
            throw new InvalidOperationException("Der vorherige Energiemodus ist nicht lesbar – Rückgängig nicht möglich.");
        _modes.Set(_source, old);
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        _modes.Get(_source) == _target ? DriftState.Unchanged : DriftState.Changed;
}
