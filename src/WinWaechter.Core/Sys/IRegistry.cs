using Microsoft.Win32;

namespace WinWaechter.Core.Sys;

public enum Hive { CurrentUser, LocalMachine }

public interface IRegistry
{
    IReadOnlyList<string> ValueNames(Hive h, string key);
    object? Get(Hive h, string key, string name);
    void Set(Hive h, string key, string name, object value, RegistryValueKind kind);
    void Delete(Hive h, string key, string name);
    void Export(Hive h, string key, string file);   // reg.exe export /y
}
