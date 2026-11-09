using Microsoft.Win32;

namespace WinWaechter.Core.Sys;

public enum Hive { CurrentUser, LocalMachine }

public interface IRegistry
{
    bool KeyExists(Hive h, string key);
    IReadOnlyList<string> ValueNames(Hive h, string key);
    object? Get(Hive h, string key, string name);
    void Set(Hive h, string key, string name, object value, RegistryValueKind kind);
    void Delete(Hive h, string key, string name);
    /// <summary>Value kind; null when key or value is missing.</summary>
    RegistryValueKind? GetKind(Hive h, string key, string name);

    /// <summary>Names of direct subkeys; empty when the key is missing.</summary>
    IReadOnlyList<string> SubKeyNames(Hive h, string key);

    void Export(Hive h, string key, string file);   // reg.exe export /y
}
