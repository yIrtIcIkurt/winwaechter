using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.ContextMenu;

/// <summary>The fixed places the context-menu cleanup may touch.</summary>
public static class ShellExtLocations
{
    public static readonly (string Key, string Label)[] All =
    [
        ("*", "Dateien"),
        ("AllFilesystemObjects", "Dateien und Ordner"),
        ("Directory", "Ordner"),
        ("Directory\\Background", "Hintergrund"),
        ("Folder", "Ordner"),
        ("Drive", "Laufwerke"),
    ];

    public const string BlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    public const string LegacyDisableValue = "LegacyDisable";

    public static readonly (Hive Hive, string Root)[] ClassRoots =
    [
        (Hive.LocalMachine, @"SOFTWARE\Classes"),
        (Hive.CurrentUser, @"Software\Classes"),
    ];
}

/// <summary>
/// Removes one entry from the Explorer context menu with Windows' own switches: a CLSID under
/// Shell Extensions\Blocked (handlers) or the LegacyDisable value on a verb key (static commands).
/// Undo deletes exactly that value.
/// </summary>
public sealed class ShellExtChange : IChange
{
    readonly IRegistry _reg;
    readonly Hive _hive;
    readonly string _key;
    readonly string _name;
    readonly string _description;

    ShellExtChange(IRegistry reg, Hive hive, string key, string name, string description)
    {
        _reg = reg; _hive = hive; _key = key; _name = name; _description = description;
    }

    public static ShellExtChange Block(IRegistry reg, Guid clsid, string title) =>
        new(reg, Hive.LocalMachine, ShellExtLocations.BlockedKey, clsid.ToString("B"), DescriptionFor(title));

    public static ShellExtChange LegacyDisable(IRegistry reg, Hive hive, string verbKey, string title) =>
        new(reg, hive, verbKey, ShellExtLocations.LegacyDisableValue, DescriptionFor(title));

    static string DescriptionFor(string title) => $"„{title}“ aus dem Rechtsklick-Menü entfernen";

    public string Kind => "shellext";
    public string Target => $"{RegDword.HiveName(_hive)}\\{_key}\\{_name}";
    public string Description => _description;

    public static ShellExtChange FromRecord(ChangeRecord r, IRegistry reg)
    {
        var hive = r.Before.GetValueOrDefault("hive") switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw NotListed(),
        };
        var key = r.Before.GetValueOrDefault("key") ?? "";
        var name = r.Before.GetValueOrDefault("name") ?? "";

        if (name == ShellExtLocations.LegacyDisableValue)
        {
            if (!IsVerbKey(hive, key)) throw NotListed();
        }
        else
        {
            if (hive != Hive.LocalMachine
                || !string.Equals(key, ShellExtLocations.BlockedKey, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(name, "B", out var clsid)
                || !string.Equals(clsid.ToString("B"), name, StringComparison.OrdinalIgnoreCase))
                throw NotListed();
            key = ShellExtLocations.BlockedKey;
            name = clsid.ToString("B");
        }
        return new ShellExtChange(reg, hive, key, name, r.Description);
    }

    /// <summary>The verb key: <c>&lt;class root of the hive&gt;\&lt;fixed location&gt;\shell\&lt;one verb segment&gt;</c>.</summary>
    public static bool IsVerbKey(Hive hive, string key)
    {
        foreach (var (h, root) in ShellExtLocations.ClassRoots)
        {
            if (h != hive) continue;
            foreach (var (loc, _) in ShellExtLocations.All)
            {
                var prefix = $"{root}\\{loc}\\shell\\";
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsVerbName(key[prefix.Length..]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>One registry key segment: no separators, no "..", no control characters.</summary>
    public static bool IsVerbName(string verb) =>
        verb.Length > 0 && !verb.Contains('\\') && !verb.Contains('/') && !verb.Contains("..", StringComparison.Ordinal)
        && !verb.Any(char.IsControl);

    public static void Register(ChangeFactory f, IRegistry reg) =>
        f.Register("shellext", r => FromRecord(r, reg));

    static InvalidOperationException NotListed() =>
        new("Dieser Eintrag gehört nicht zum Rechtsklick-Menü – Rückgängig abgelehnt.");

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = RegDword.Snapshot(_reg, _hive, _key, _name);
        if (before["existed"] == "true")
            throw new InvalidOperationException("Der Eintrag ist schon abgeschaltet (der Wert ist bereits gesetzt).");
        // a static command's key must still exist; writing into a missing one would leave a stray key behind
        if (_name == ShellExtLocations.LegacyDisableValue && !_reg.KeyExists(_hive, _key))
            throw new InvalidOperationException("Der Eintrag existiert nicht mehr.");

        RegDword.WriteString(_reg, _hive, _key, _name, "");
        var after = new Dictionary<string, string?>
        {
            ["value"] = "",
            ["kind"] = nameof(RegistryValueKind.String),
        };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new() { ["hive"] = RegDword.HiveName(_hive), ["key"] = _key, ["name"] = _name }, new());

    // The journal never decides what is deleted: always exactly the value this change owns.
    public void Undo(ChangeRecord record, SessionContext ctx) =>
        RegDword.Restore(_reg, _hive, _key, _name, new Dictionary<string, string?> { ["existed"] = "false" });

    public DriftState CheckDrift(ChangeRecord record) =>
        _reg.GetKind(_hive, _key, _name) is null
            ? DriftState.Gone
            : RegDword.DriftString(_reg, _hive, _key, _name, "");
}
