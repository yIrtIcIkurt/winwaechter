using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Shared logic for changes that set one REG_DWORD: snapshot the old value, write the target,
/// restore the old state and compare the live value with the target.
/// </summary>
public static class RegDword
{
    public static string HiveName(Hive h) => h == Hive.CurrentUser ? "HKCU" : "HKLM";

    /// <summary>Old state as journal "Before" data. Refuses value types other than DWORD/QWORD/String/ExpandString.</summary>
    public static Dictionary<string, string?> Snapshot(IRegistry reg, Hive hive, string key, string name)
    {
        var kind = reg.GetKind(hive, key, name);
        if (kind is not null && kind is not (RegistryValueKind.DWord or RegistryValueKind.QWord
            or RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidOperationException(
                $"Der Wert „{name}“ hat einen unerwarteten Typ ({kind}) und wird nicht verändert.");

        var old = kind is null ? null : reg.Get(hive, key, name);
        return new Dictionary<string, string?>
        {
            ["hive"] = HiveName(hive),
            ["key"] = key,
            ["name"] = name,
            ["existed"] = kind is null ? "false" : "true",
            ["value"] = old is null ? null : Convert.ToString(old, CultureInfo.InvariantCulture),
            ["kind"] = kind?.ToString(),
        };
    }

    /// <summary>Writes the DWORD; creates the key first when it is missing.</summary>
    public static void Write(IRegistry reg, Hive hive, string key, string name, int value)
    {
        if (!reg.KeyExists(hive, key)) reg.CreateKey(hive, key);
        reg.Set(hive, key, name, value, RegistryValueKind.DWord);
    }

    public static void Restore(IRegistry reg, Hive hive, string key, string name, IReadOnlyDictionary<string, string?> before)
    {
        if (before.GetValueOrDefault("existed") != "true")
        {
            reg.Delete(hive, key, name);
            return;
        }
        var text = before.GetValueOrDefault("value") ?? "";
        var kind = Enum.Parse<RegistryValueKind>(before.GetValueOrDefault("kind") ?? "");
        switch (kind)
        {
            case RegistryValueKind.DWord:
                reg.Set(hive, key, name, int.Parse(text, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.QWord:
                reg.Set(hive, key, name, long.Parse(text, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.String or RegistryValueKind.ExpandString:
                reg.Set(hive, key, name, text, kind);
                break;
            default:
                throw new InvalidOperationException($"Typ {kind} kann nicht wiederhergestellt werden.");
        }
    }

    public static DriftState Drift(IRegistry reg, Hive hive, string key, string name, int target) =>
        reg.GetKind(hive, key, name) == RegistryValueKind.DWord
        && reg.Get(hive, key, name) is { } v
        && Convert.ToInt32(v, CultureInfo.InvariantCulture) == target
            ? DriftState.Unchanged : DriftState.Changed;
}
