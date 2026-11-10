using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>
/// Removes one uninstall entry (a direct child of one of the three uninstall roots, nothing else).
/// The key with its values and subkeys is saved natively as JSON into the session backup first; the
/// journal keeps the SHA-256 of that file. Undo recreates the key only when the file is trusted and
/// its hash still matches. No reg.exe.
/// </summary>
public sealed class RegKeyChange : IChange
{
    public const int MaxDepth = 8;

    const string MissingText = "Der Eintrag existiert nicht mehr. Es wurde nichts geändert.";
    const string ExistsAgainText = "Der Eintrag existiert wieder. Nichts wiederhergestellt.";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly IRegistry _reg;
    readonly Hive _hive;
    readonly string _key;
    readonly string _displayName;
    string? _plannedId;

    public RegKeyChange(IRegistry reg, Hive hive, string key, string displayName)
    {
        if (!UninstallRoots.IsDirectChild(hive, key))
            throw new ArgumentException("Nur Deinstallations-Einträge direkt unter den Uninstall-Schlüsseln werden entfernt.", nameof(key));
        _reg = reg; _hive = hive; _key = key; _displayName = displayName;
    }

    public static RegKeyChange FromRecord(ChangeRecord r, IRegistry reg)
    {
        var hive = r.Before.GetValueOrDefault("hive") switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw NotAllowed(),
        };
        var key = r.Before.GetValueOrDefault("key");
        if (!UninstallRoots.IsDirectChild(hive, key)) throw NotAllowed();
        return new RegKeyChange(reg, hive, key!, r.Before.GetValueOrDefault("name") ?? KeyName(key!));
    }

    static InvalidOperationException NotAllowed() =>
        new("Dieser Registry-Schlüssel ist kein Deinstallations-Eintrag – abgelehnt.");

    public string Kind => "regkey";
    public string Target => $"{HiveName(_hive)}\\{_key}";
    public string Description => $"Deinstallations-Eintrag „{_displayName}“ entfernen";

    static string HiveName(Hive h) => h == Hive.CurrentUser ? "HKCU" : "HKLM";
    static string KeyName(string key) => key[(key.LastIndexOf('\\') + 1)..];

    Dictionary<string, string?> BeforeValues() => new()
    {
        ["hive"] = HiveName(_hive),
        ["key"] = _key,
        ["name"] = _displayName,
    };

    static string BackupPath(SessionContext ctx, string id) => Path.Combine(Path.GetFullPath(ctx.BackupDir), $"regkey_{id}.json");

    /// <summary>The backup path goes into the Pending record before anything is written.</summary>
    public ChangeRecord Plan(SessionContext ctx)
    {
        _plannedId = Guid.NewGuid().ToString("N");
        return new ChangeRecord(_plannedId, Kind, Target, Description, BeforeValues(),
            new() { ["backup"] = BackupPath(ctx, _plannedId) }, ChangeStatus.Pending, DateTimeOffset.Now);
    }

    public ChangeRecord Apply(SessionContext ctx)
    {
        var id = _plannedId ?? Guid.NewGuid().ToString("N");
        _plannedId = null;
        if (!UninstallRoots.IsDirectChild(_hive, _key)) throw NotAllowed();
        if (!_reg.KeyExists(_hive, _key)) throw new InvalidOperationException(MissingText);

        var snapshot = Read(_key, KeyName(_key), 0);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var backup = BackupPath(ctx, id);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        // CreateNew: never write into a file that is already there
        using (var fs = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        ctx.ClaimFile(backup);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        try
        {
            _reg.DeleteKeyTree(_hive, _key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // partly deleted (e.g. access denied on a subkey): put back what is gone from the snapshot in memory
            FileLog.Error($"Deinstallations-Eintrag nicht entfernt: {Target}", ex);
            var restored = false;
            try { Write(snapshot); restored = true; }
            catch (Exception ex2) { FileLog.Error($"Deinstallations-Eintrag nicht zurückgeschrieben: {Target}", ex2); }
            throw new InvalidOperationException(restored
                ? $"Der Eintrag konnte nicht entfernt werden ({ex.Message}). Er wurde unverändert zurückgeschrieben."
                : $"Der Eintrag konnte nicht vollständig entfernt werden ({ex.Message}). Die Sicherung liegt unter {backup}.", ex);
        }

        return new ChangeRecord(id, Kind, Target, Description, BeforeValues(),
            new() { ["backup"] = backup, ["sha256"] = hash }, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    // Values and subkeys of key; refuses (before anything is written) what it cannot restore exactly.
    RegKeySnapshot Read(string key, string name, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException("Der Eintrag ist zu tief verschachtelt und wird nicht entfernt. Es wurde nichts geändert.");
        var values = new List<RegValueSnapshot>();
        foreach (var valueName in _reg.ValueNames(_hive, key))
        {
            var kind = _reg.GetKind(_hive, key, valueName);
            var data = _reg.Get(_hive, key, valueName);
            if (kind is null || data is null || kind == RegistryValueKind.Unknown)
                throw new InvalidOperationException(
                    $"Der Wert „{valueName}“ des Eintrags kann nicht vollständig gesichert werden. Es wurde nichts geändert.");
            values.Add(new RegValueSnapshot(valueName, kind.Value.ToString(), Encode(kind.Value, data)));
        }
        var subKeys = new List<RegKeySnapshot>();
        foreach (var sub in _reg.SubKeyNames(_hive, key))
        {
            if (!IsPlainName(sub))
                throw new InvalidOperationException("Der Eintrag enthält einen ungültigen Unterschlüssel. Es wurde nichts geändert.");
            subKeys.Add(Read(key + "\\" + sub, sub, depth + 1));
        }
        return new RegKeySnapshot(name, values, subKeys);
    }

    static bool IsPlainName(string? name) => !string.IsNullOrEmpty(name) && !name.Contains('\\') && name is not ("." or "..");

    static string Encode(RegistryValueKind kind, object data) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => (string)data,
        RegistryValueKind.DWord => Convert.ToInt32(data, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(data, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])data),
        _ => Convert.ToBase64String((byte[])data),   // Binary, None
    };

    static object Decode(RegValueSnapshot v, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => v.Data,
        RegistryValueKind.DWord => int.Parse(v.Data, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(v.Data, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(v.Data) is { } a && a.All(x => x is not null)
            ? a : throw new FormatException(),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(v.Data),
        _ => throw new FormatException(),
    };

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (!UninstallRoots.IsDirectChild(_hive, _key)) throw NotAllowed();

        var backup = record.After.GetValueOrDefault("backup");
        var expected = record.After.GetValueOrDefault("sha256");
        if (string.IsNullOrWhiteSpace(backup))
            throw new InvalidOperationException("Die Sicherung des Eintrags fehlt, Wiederherstellung nicht möglich.");
        var full = Path.GetFullPath(backup);
        // Apply writes the backup before it deletes anything: no hash and no file means nothing was removed.
        if (expected is null && !File.Exists(full)) return;
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(ctx.BackupDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Sicherung des Eintrags liegt außerhalb der Sitzung, Wiederherstellung abgelehnt.");
        if (!File.Exists(full))
            throw new InvalidOperationException("Die Sicherung des Eintrags fehlt, Wiederherstellung nicht möglich.");
        if (!ctx.IsTrustedFile(full))
            throw new InvalidOperationException("Die Sicherung des Eintrags stammt nicht von WinWächter, Wiederherstellung abgelehnt.");

        // hash and parse the very same bytes
        var bytes = File.ReadAllBytes(full);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expected is null || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Sicherung des Eintrags wurde verändert, Wiederherstellung abgelehnt.");

        RegKeySnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<RegKeySnapshot>(bytes, Json); }
        catch (JsonException) { snapshot = null; }
        if (snapshot is null || !string.Equals(snapshot.Name, KeyName(_key), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Sicherung passt nicht zu diesem Eintrag, Wiederherstellung abgelehnt.");

        if (_reg.KeyExists(_hive, _key)) throw new InvalidOperationException(ExistsAgainText);
        Write(snapshot);
    }

    // Checks the whole snapshot first, then recreates keys and values below _key.
    void Write(RegKeySnapshot snapshot)
    {
        var writes = new List<(string Key, List<(string Name, object Value, RegistryValueKind Kind)> Values)>();
        Prepare(snapshot, _key, 0, writes);
        foreach (var (key, values) in writes)
        {
            _reg.CreateKey(_hive, key);
            foreach (var (name, value, kind) in values) _reg.Set(_hive, key, name, value, kind);
        }
    }

    static void Prepare(RegKeySnapshot s, string key, int depth,
        List<(string Key, List<(string Name, object Value, RegistryValueKind Kind)> Values)> writes)
    {
        if (depth > MaxDepth || s.Values is null || s.SubKeys is null) throw Broken();
        var values = new List<(string, object, RegistryValueKind)>();
        foreach (var v in s.Values)
        {
            if (v?.Name is null || v.Data is null || !Enum.TryParse<RegistryValueKind>(v.Kind, out var kind)
                || !Enum.IsDefined(kind) || kind == RegistryValueKind.Unknown)
                throw Broken();
            try { values.Add((v.Name, Decode(v, kind), kind)); }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException) { throw Broken(); }
        }
        writes.Add((key, values));
        foreach (var sub in s.SubKeys)
        {
            if (sub is null || !IsPlainName(sub.Name)) throw Broken();
            Prepare(sub, key + "\\" + sub.Name, depth + 1, writes);
        }
    }

    static InvalidOperationException Broken() =>
        new("Die Sicherung des Eintrags ist beschädigt, Wiederherstellung abgelehnt.");

    public DriftState CheckDrift(ChangeRecord record) =>
        _reg.KeyExists(_hive, _key) ? DriftState.Changed : DriftState.Unchanged;
}

internal sealed record RegKeySnapshot(string Name, List<RegValueSnapshot> Values, List<RegKeySnapshot> SubKeys);

internal sealed record RegValueSnapshot(string Name, string Kind, string Data);
