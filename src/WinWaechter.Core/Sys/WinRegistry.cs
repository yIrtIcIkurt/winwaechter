using System.Diagnostics;
using Microsoft.Win32;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Real registry, 64-bit view. CurrentUser means the interactive user's hive (HKU\&lt;SID&gt;),
/// not the HKCU of the (possibly different) elevated process.
/// </summary>
public sealed class WinRegistry(IUserContext user) : IRegistry
{
    public bool KeyExists(Hive h, string key)
    {
        using var k = Open(h, key, writable: false);
        return k is not null;
    }

    public IReadOnlyList<string> ValueNames(Hive h, string key)
    {
        using var k = Open(h, key, writable: false);
        return k?.GetValueNames() ?? [];
    }

    // Raw value: REG_EXPAND_SZ stays unexpanded so it can be written back unchanged.
    public object? Get(Hive h, string key, string name)
    {
        using var k = Open(h, key, writable: false);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Set(Hive h, string key, string name, object value, RegistryValueKind kind)
    {
        using var root = Base(h);
        using var k = root.CreateSubKey(SubPath(h, key), writable: true);
        k.SetValue(name, value, kind);
    }

    public void Delete(Hive h, string key, string name)
    {
        using var k = Open(h, key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void Export(Hive h, string key, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "export", RegPath(h, key), file, "/y", "/reg:64" }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("reg.exe konnte nicht gestartet werden.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        _ = stdout.Result;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"reg.exe export fehlgeschlagen ({p.ExitCode}) für {RegPath(h, key)}: {stderr.Trim()}");
    }

    RegistryKey Base(Hive h) =>
        RegistryKey.OpenBaseKey(h == Hive.CurrentUser ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64);

    string SubPath(Hive h, string key)
    {
        key = key.Trim('\\');
        return h == Hive.CurrentUser ? $@"{user.Sid}\{key}" : key;
    }

    string RegPath(Hive h, string key) => (h == Hive.CurrentUser ? @"HKU\" : @"HKLM\") + SubPath(h, key);

    RegistryKey? Open(Hive h, string key, bool writable)
    {
        using var root = Base(h);
        return root.OpenSubKey(SubPath(h, key), writable);
    }
}
