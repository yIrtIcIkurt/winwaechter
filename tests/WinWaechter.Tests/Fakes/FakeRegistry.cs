using Microsoft.Win32;
using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// In-memory registry; key and value names are case-insensitive like the real one.
public sealed class FakeRegistry : IRegistry
{
    readonly Dictionary<(Hive, string), Dictionary<string, (object Value, RegistryValueKind Kind)>> _keys = new(new KeyComparer());

    public List<(Hive Hive, string Key, string Name, object Value, RegistryValueKind Kind)> SetCalls { get; } = [];
    public List<(Hive Hive, string Key, string Name)> DeleteCalls { get; } = [];
    public List<(Hive Hive, string Key, string File)> ExportCalls { get; } = [];
    public List<(Hive Hive, string Key)> CreateKeyCalls { get; } = [];
    public List<(Hive Hive, string Key)> DeleteTreeCalls { get; } = [];

    /// <summary>SubKeyNames, ValueNames and Get on these keys throw (simulates an unreadable key).</summary>
    public HashSet<string> ThrowingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    void ThrowIfUnreadable(string key)
    {
        if (ThrowingKeys.Contains(key.Trim('\\'))) throw new System.Security.SecurityException($"Zugriff verweigert: {key}");
    }

    // Pre-fills a value without recording a call.
    public FakeRegistry Seed(Hive h, string key, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        Values(h, key, create: true)![name] = (value, kind);
        return this;
    }

    // Creates an empty key.
    public FakeRegistry SeedKey(Hive h, string key)
    {
        Values(h, key, create: true);
        return this;
    }

    public RegistryValueKind? GetKind(Hive h, string key, string name) => KindOf(h, key, name);

    // Direct children of key, derived from all known key paths.
    public IReadOnlyList<string> SubKeyNames(Hive h, string key)
    {
        ThrowIfUnreadable(key);
        var prefix = key.Trim('\\') + "\\";
        return _keys.Keys
            .Where(k => k.Item1 == h && k.Item2.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Item2[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public RegistryValueKind? KindOf(Hive h, string key, string name) =>
        Values(h, key, create: false)?.TryGetValue(name, out var v) == true ? v.Kind : null;

    public bool KeyExists(Hive h, string key) => Values(h, key, create: false) is not null;

    public IReadOnlyList<string> ValueNames(Hive h, string key)
    {
        ThrowIfUnreadable(key);
        return Values(h, key, create: false)?.Keys.ToList() ?? [];
    }

    public object? Get(Hive h, string key, string name)
    {
        ThrowIfUnreadable(key);
        return Values(h, key, create: false)?.TryGetValue(name, out var v) == true ? v.Value : null;
    }

    public void Set(Hive h, string key, string name, object value, RegistryValueKind kind)
    {
        SetCalls.Add((h, key, name, value, kind));
        Values(h, key, create: true)![name] = (value, kind);
    }

    public void Delete(Hive h, string key, string name)
    {
        DeleteCalls.Add((h, key, name));
        Values(h, key, create: false)?.Remove(name);
    }

    public void CreateKey(Hive h, string key)
    {
        CreateKeyCalls.Add((h, key));
        Values(h, key, create: true);
    }

    /// <summary>When set, DeleteKeyTree removes only the subkeys and then throws it (partly deleted tree).</summary>
    public Exception? DeleteTreeFailsMidway { get; set; }

    // Removes the key and every key below it.
    public void DeleteKeyTree(Hive h, string key)
    {
        DeleteTreeCalls.Add((h, key));
        var k = key.Trim('\\');
        foreach (var victim in _keys.Keys.Where(x => x.Item1 == h && (string.Equals(x.Item2, k, StringComparison.OrdinalIgnoreCase)
                     || x.Item2.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))).ToList())
        {
            if (DeleteTreeFailsMidway is not null && string.Equals(victim.Item2, k, StringComparison.OrdinalIgnoreCase)) continue;
            _keys.Remove(victim);
        }
        if (DeleteTreeFailsMidway is { } e) throw e;
    }

    // Writes a small marker file so callers can check the backup exists.
    public void Export(Hive h, string key, string file)
    {
        ExportCalls.Add((h, key, file));
        if (!KeyExists(h, key)) throw new InvalidOperationException($"reg.exe export fehlgeschlagen: Schlüssel fehlt {h}\\{key}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"Windows Registry Editor Version 5.00\r\n\r\n[{h}\\{key}]\r\n");
    }

    Dictionary<string, (object Value, RegistryValueKind Kind)>? Values(Hive h, string key, bool create)
    {
        var k = (h, key.Trim('\\'));
        if (_keys.TryGetValue(k, out var v)) return v;
        if (!create) return null;
        return _keys[k] = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class KeyComparer : IEqualityComparer<(Hive, string)>
    {
        public bool Equals((Hive, string) a, (Hive, string) b) =>
            a.Item1 == b.Item1 && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Hive, string) k) =>
            HashCode.Combine(k.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }
}
