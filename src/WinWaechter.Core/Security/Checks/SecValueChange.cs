using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>
/// Sets one registry value from the fixed security list (SecurityValues) to its listed target.
/// Neither the value nor the target is ever taken from a journal without checking it against the list.
/// </summary>
public sealed class SecValueChange(IRegistry reg, SecValue value, string description) : IChange
{
    public string Kind => "secvalue";
    public string Target => $"{RegDword.HiveName(value.Hive)}\\{value.Key}\\{value.Name}";
    public string Description => description;

    public static SecValueChange FromRecord(ChangeRecord r, IRegistry reg)
    {
        var hive = r.Before.GetValueOrDefault("hive") switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw NotListed(),
        };
        var value = SecurityValues.Find(hive, r.Before.GetValueOrDefault("key") ?? "", r.Before.GetValueOrDefault("name") ?? "")
            ?? throw NotListed();
        return new SecValueChange(reg, value, r.Description);
    }

    static InvalidOperationException NotListed() =>
        new("Dieser Wert gehört nicht zu den Sicherheits-Einstellungen – Rückgängig abgelehnt.");

    public static void Register(ChangeFactory f, IRegistry reg) => f.Register("secvalue", r => FromRecord(r, reg));

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = RegDword.Snapshot(reg, value.Hive, value.Key, value.Name);
        if (value.Kind == RegistryValueKind.String)
            RegDword.WriteString(reg, value.Hive, value.Key, value.Name, (string)value.Target);
        else
            RegDword.Write(reg, value.Hive, value.Key, value.Name, (int)value.Target);

        var after = new Dictionary<string, string?>
        {
            ["value"] = Convert.ToString(value.Target, CultureInfo.InvariantCulture),
            ["kind"] = value.Kind.ToString(),
        };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new() { ["hive"] = RegDword.HiveName(value.Hive), ["key"] = value.Key, ["name"] = value.Name },
         new() { ["value"] = Convert.ToString(value.Target, CultureInfo.InvariantCulture) });

    public void Undo(ChangeRecord record, SessionContext ctx) =>
        RegDword.Restore(reg, value.Hive, value.Key, value.Name, record.Before);

    public DriftState CheckDrift(ChangeRecord record) =>
        value.Kind == RegistryValueKind.String
            ? RegDword.DriftString(reg, value.Hive, value.Key, value.Name, (string)value.Target)
            : RegDword.Drift(reg, value.Hive, value.Key, value.Name, (int)value.Target);
}
