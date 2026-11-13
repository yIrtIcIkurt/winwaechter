using System.ComponentModel;
using System.Runtime.InteropServices;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Performance;

public sealed record PowerPlan(Guid Id, string Name);

public interface IPowerPlans
{
    IReadOnlyList<PowerPlan> List();

    /// <summary>The active plan; null when it cannot be read.</summary>
    Guid? Active();

    void SetActive(Guid id);
}

/// <summary>The installed power plans through powrprof (never the registry, never powercfg).</summary>
public sealed class WinPowerPlans : IPowerPlans
{
    const uint NoMoreItems = 259;

    public IReadOnlyList<PowerPlan> List()
    {
        var plans = new List<PowerPlan>();
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; i < 256; i++)
            {
                uint size = 16;
                var code = PowerNative.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, PowerNative.AccessScheme, i, buffer, ref size);
                if (code == NoMoreItems) break;
                if (code != 0) throw new Win32Exception((int)code);
                var id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add(new PowerPlan(id, FriendlyName(id)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return plans;
    }

    static string FriendlyName(Guid id)
    {
        uint size = 0;
        PowerNative.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size < 2 || size > 4096) return id.ToString("D");
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerNative.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0)
                return id.ToString("D");
            var name = Marshal.PtrToStringUni(buf);
            return string.IsNullOrWhiteSpace(name) ? id.ToString("D") : name;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public Guid? Active()
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

    public void SetActive(Guid id)
    {
        var code = PowerNative.PowerSetActiveScheme(IntPtr.Zero, ref id);
        if (code != 0) throw new Win32Exception((int)code);
    }
}

/// <summary>Switches the active power plan to one of the plans installed right now.</summary>
public sealed class PowerPlanChange : IChange
{
    readonly IPowerPlans _plans;
    readonly PowerPlan _target;

    public PowerPlanChange(IPowerPlans plans, Guid target)
        : this(plans, plans.List().FirstOrDefault(p => p.Id == target)
            ?? throw new ArgumentException("Dieser Energiesparplan ist nicht vorhanden.", nameof(target)))
    {
    }

    PowerPlanChange(IPowerPlans plans, PowerPlan target)
    {
        _target = target;
        _plans = plans;
    }

    public string Kind => "powerplan";
    public string Target => "Energiesparplan";
    public string Description => $"Energiesparplan auf „{_target.Name}“ umstellen";

    /// <summary>
    /// Only the target's id has to be readable: Undo activates the old plan only when it is installed, Apply (also a
    /// retry, whose Before is empty) the target only when it is installed – so undo still works after the target was deleted.
    /// </summary>
    public static PowerPlanChange FromRecord(ChangeRecord r, IPowerPlans plans)
    {
        if (!Guid.TryParse(r.After.GetValueOrDefault("plan"), out var target) || target == Guid.Empty)
            throw new InvalidOperationException("Dieser Energiesparplan ist nicht lesbar – Rückgängig abgelehnt.");
        // the name only for the text; a deleted plan is shown by its id, as Windows does for a plan without a name
        var plan = plans.List().FirstOrDefault(p => p.Id == target) ?? new PowerPlan(target, target.ToString("D"));
        return new PowerPlanChange(plans, plan);
    }

    public ChangeRecord Apply(SessionContext ctx)
    {
        // Without a readable old plan the change could never be undone, so nothing is switched.
        var active = _plans.Active()
            ?? throw new InvalidOperationException("Der aktive Energiesparplan ist nicht lesbar – WinWächter stellt ihn deshalb nicht um.");
        if (_plans.List().All(p => p.Id != _target.Id))
            throw new InvalidOperationException("Der Energiesparplan ist nicht mehr vorhanden – WinWächter stellt nicht um.");
        var before = new Dictionary<string, string?> { ["plan"] = active.ToString("D") };
        _plans.SetActive(_target.Id);
        var after = new Dictionary<string, string?> { ["plan"] = _target.Id.ToString("D") };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new(), new() { ["plan"] = _target.Id.ToString("D") });

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (!Guid.TryParse(record.Before.GetValueOrDefault("plan"), out var old) || _plans.List().All(p => p.Id != old))
            throw new InvalidOperationException("Der vorherige Energiesparplan ist nicht mehr vorhanden – Rückgängig nicht möglich.");
        _plans.SetActive(old);
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        _plans.Active() == _target.Id ? DriftState.Unchanged : DriftState.Changed;
}
