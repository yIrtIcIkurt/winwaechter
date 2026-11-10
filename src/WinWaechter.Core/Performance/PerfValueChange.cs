using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>
/// Sets one registry DWORD from the fixed performance whitelist (PerfValues) to one of its allowed targets.
/// Neither the value nor the target is ever taken from a journal without checking it against the whitelist.
/// </summary>
public sealed class PerfValueChange : IChange
{
    readonly IRegistry _reg;
    readonly PerfValue _value;
    readonly int _target;
    readonly string _description;
    readonly ISettingBroadcast? _broadcast;

    public PerfValueChange(IRegistry reg, PerfValue value, int target, string description, ISettingBroadcast? broadcast)
    {
        if (!value.Targets.Contains(target))
            throw new ArgumentException($"Der Zielwert {target} ist für „{value.Name}“ nicht erlaubt.", nameof(target));
        _reg = reg; _value = value; _target = target; _description = description; _broadcast = broadcast;
    }

    public string Kind => "perfvalue";
    public string Target => $"{RegDword.HiveName(_value.Hive)}\\{_value.Key}\\{_value.Name}";
    public string Description => _description;

    public static PerfValueChange FromRecord(ChangeRecord r, IRegistry reg, ISettingBroadcast broadcast)
    {
        var hive = r.Before.GetValueOrDefault("hive") switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw NotListed(),
        };
        var value = PerfValues.Find(hive, r.Before.GetValueOrDefault("key") ?? "", r.Before.GetValueOrDefault("name") ?? "")
            ?? throw NotListed();
        if (!int.TryParse(r.After.GetValueOrDefault("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
            || !value.Targets.Contains(target))
            throw NotListed();
        return new PerfValueChange(reg, value, target, r.Description, broadcast);
    }

    static InvalidOperationException NotListed() =>
        new("Dieser Wert gehört nicht zu den Leistungs-Einstellungen – Rückgängig abgelehnt.");

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = RegDword.Snapshot(_reg, _value.Hive, _value.Key, _value.Name);
        RegDword.Write(_reg, _value.Hive, _value.Key, _value.Name, _target);
        Notify();

        var after = new Dictionary<string, string?>
        {
            ["value"] = _target.ToString(CultureInfo.InvariantCulture),
            ["kind"] = nameof(RegistryValueKind.DWord),
        };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        RegDword.Restore(_reg, _value.Hive, _value.Key, _value.Name, record.Before);
        Notify();
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        RegDword.Drift(_reg, _value.Hive, _value.Key, _value.Name, _target);

    void Notify()
    {
        if (_value.Broadcast) _broadcast?.ImmersiveColorSetChanged();
    }
}
