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
        var kind = _reg.GetKind(_value.Hive, _value.Key, _value.Name);
        if (kind is not null && kind is not (RegistryValueKind.DWord or RegistryValueKind.QWord
            or RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidOperationException(
                $"Der Wert „{_value.Name}“ hat einen unerwarteten Typ ({kind}) und wird nicht verändert.");

        var old = kind is null ? null : _reg.Get(_value.Hive, _value.Key, _value.Name);
        var before = new Dictionary<string, string?>
        {
            ["hive"] = HiveName(_value.Hive),
            ["key"] = _value.Key,
            ["name"] = _value.Name,
            ["existed"] = kind is null ? "false" : "true",
            ["value"] = old is null ? null : Convert.ToString(old, CultureInfo.InvariantCulture),
            ["kind"] = kind?.ToString(),
        };

        _reg.Set(_value.Hive, _value.Key, _value.Name, _value.Target, RegistryValueKind.DWord);

        var after = new Dictionary<string, string?>
        {
            ["value"] = _value.Target.ToString(CultureInfo.InvariantCulture),
            ["kind"] = nameof(RegistryValueKind.DWord),
        };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (record.Before.GetValueOrDefault("existed") != "true")
        {
            _reg.Delete(_value.Hive, _value.Key, _value.Name);
            return;
        }
        var text = record.Before.GetValueOrDefault("value") ?? "";
        var kind = Enum.Parse<RegistryValueKind>(record.Before.GetValueOrDefault("kind") ?? "");
        switch (kind)
        {
            case RegistryValueKind.DWord:
                _reg.Set(_value.Hive, _value.Key, _value.Name, int.Parse(text, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.QWord:
                _reg.Set(_value.Hive, _value.Key, _value.Name, long.Parse(text, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.String or RegistryValueKind.ExpandString:
                _reg.Set(_value.Hive, _value.Key, _value.Name, text, kind);
                break;
            default:
                throw new InvalidOperationException($"Typ {kind} kann nicht wiederhergestellt werden.");
        }
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        _reg.GetKind(_value.Hive, _value.Key, _value.Name) == RegistryValueKind.DWord
        && _reg.Get(_value.Hive, _value.Key, _value.Name) is { } v
        && Convert.ToInt32(v, CultureInfo.InvariantCulture) == _value.Target
            ? DriftState.Unchanged : DriftState.Changed;

    static string HiveName(Hive h) => h == Hive.CurrentUser ? "HKCU" : "HKLM";
}
