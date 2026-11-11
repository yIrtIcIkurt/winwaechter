using System.Runtime.InteropServices;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
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
    /// <summary>False when this Windows version lacks the power mode API; the modes can then not be read or set.</summary>
    bool Supported { get; }

    bool HasBattery { get; }
    PowerSource Current { get; }

    /// <summary>The active power plan; null when it cannot be read.</summary>
    Guid? ActivePlan { get; }

    /// <summary>The mode chosen for this source; null when it cannot be read.</summary>
    Guid? Get(PowerSource s);

    void Set(PowerSource s, Guid mode);
}

/// <summary>The powrprof calls behind the power mode: Get returns the Win32 result (0 = success) and the mode,
/// Set the Win32 result. A missing function throws <see cref="EntryPointNotFoundException"/>.</summary>
public sealed record PowerModeApi(Func<PowerSource, (uint Code, Guid Mode)> Get, Func<PowerSource, Guid, uint> Set)
{
    public static PowerModeApi Native { get; } = new(
        s =>
        {
            Guid g;
            var code = s == PowerSource.Ac
                ? PowerNative.PowerGetUserConfiguredACPowerMode(out g)
                : PowerNative.PowerGetUserConfiguredDCPowerMode(out g);
            return (code, g);
        },
        (s, mode) => s == PowerSource.Ac
            ? PowerNative.PowerSetUserConfiguredACPowerMode(ref mode)
            : PowerNative.PowerSetUserConfiguredDCPowerMode(ref mode));
}

/// <summary>Reads and sets the power mode through the Windows power mode API (never the registry: its key is
/// protected even for administrators). Each source is set directly; Windows applies the inactive one at the next
/// power-source change.</summary>
/// <param name="api">Defaults to <see cref="PowerModeApi.Native"/>. Tests inject fakes.</param>
public sealed class WinPowerModes(PowerModeApi? api = null) : IPowerModes
{
    const int Unknown = 0, Yes = 1, No = 2;

    readonly PowerModeApi _api = api ?? PowerModeApi.Native;
    volatile int _supported = Unknown;

    public bool Supported
    {
        get
        {
            if (_supported == Unknown) _ = Get(PowerSource.Ac); // the probe marks a missing API
            return _supported != No;
        }
    }

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
            catch (Exception ex) when (IsMissing(ex))
            {
                return null;
            }
        }
    }

    public Guid? Get(PowerSource s)
    {
        if (_supported == No) return null;
        try
        {
            var (code, mode) = _api.Get(s);
            if (_supported == Unknown) _supported = Yes;
            return code == 0 ? mode : null;
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            _supported = No;
            return null;
        }
    }

    public void Set(PowerSource s, Guid mode)
    {
        uint code;
        try
        {
            code = _api.Set(s, mode);
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            _supported = No;
            throw new InvalidOperationException("Der Energiemodus ist auf dieser Windows-Version nicht änderbar.", ex);
        }
        if (code != 0) throw new InvalidOperationException($"Der Energiemodus ließ sich nicht setzen (Fehler {code}).");
    }

    static bool IsMissing(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException;

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
