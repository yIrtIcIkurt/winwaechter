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

    // Pre-fills a value without recording a call.
    public FakeRegistry Seed(Hive h, string key, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        Values(h, key, create: true)![name] = (value, kind);
        return this;
    }

    public RegistryValueKind? KindOf(Hive h, string key, string name) =>
        Values(h, key, create: false)?.TryGetValue(name, out var v) == true ? v.Kind : null;

    public IReadOnlyList<string> ValueNames(Hive h, string key) => Values(h, key, create: false)?.Keys.ToList() ?? [];

    public object? Get(Hive h, string key, string name) =>
        Values(h, key, create: false)?.TryGetValue(name, out var v) == true ? v.Value : null;

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

    // Writes a small marker file so callers can check the backup exists.
    public void Export(Hive h, string key, string file)
    {
        ExportCalls.Add((h, key, file));
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
