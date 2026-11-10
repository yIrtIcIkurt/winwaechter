using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Privacy;

/// <summary>
/// Sets one registry value from the fixed privacy list to its target (REG_DWORD).
/// Only (hive, key, name) triples from rules.json qualify; the target always comes from there, never from a journal.
/// </summary>
public sealed class RegValueChange : IChange
{
    readonly IRegistry _reg;
    readonly PrivacyValue _value;
    readonly string _description;

    public RegValueChange(IRegistry reg, PrivacyValue value, string settingTitle)
        : this(reg, value, $"{settingTitle}: „{value.Name}“ auf {value.Target} setzen", 0) { }

    RegValueChange(IRegistry reg, PrivacyValue value, string description, int _)
    {
        _reg = reg; _value = value; _description = description;
    }

    public string Kind => "regvalue";
    public string Target => $"{HiveName(_value.Hive)}\\{_value.Key}\\{_value.Name}";
    public string Description => _description;

    public static RegValueChange FromRecord(ChangeRecord r, IRegistry reg, RuleSet rules)
    {
        var hive = r.Before.GetValueOrDefault("hive") switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw NotListed(),
        };
        var value = rules.FindPrivacyValue(hive, r.Before.GetValueOrDefault("key") ?? "", r.Before.GetValueOrDefault("name") ?? "")
            ?? throw NotListed();
        return new RegValueChange(reg, value, r.Description, 0);
    }

    static InvalidOperationException NotListed() =>
        new("Dieser Registry-Wert steht nicht in der Datenschutz-Liste – Rückgängig abgelehnt.");

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = RegDword.Snapshot(_reg, _value.Hive, _value.Key, _value.Name);
        RegDword.Write(_reg, _value.Hive, _value.Key, _value.Name, _value.Target);

        var after = new Dictionary<string, string?>
        {
            ["value"] = _value.Target.ToString(CultureInfo.InvariantCulture),
            ["kind"] = nameof(RegistryValueKind.DWord),
        };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx) =>
        RegDword.Restore(_reg, _value.Hive, _value.Key, _value.Name, record.Before);

    public DriftState CheckDrift(ChangeRecord record) =>
        RegDword.Drift(_reg, _value.Hive, _value.Key, _value.Name, _value.Target);

    static string HiveName(Hive h) => RegDword.HiveName(h);
}
