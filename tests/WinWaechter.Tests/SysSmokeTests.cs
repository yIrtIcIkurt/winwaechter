using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

// Real implementations. Read-only against the system; writes only to a test-owned HKCU key and temp dirs.
public sealed class SysSmokeTests : IDisposable
{
    const string TestRoot = @"Software\WinWaechterTests";
    readonly string _key = $@"{TestRoot}\{Guid.NewGuid():N}";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ww-sys-" + Guid.NewGuid().ToString("N"));

    public SysSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_key, false); } catch { }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(TestRoot);
            if (root is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(TestRoot, false);
        }
        catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void Interactive_user_is_a_real_account_with_profile()
    {
        var u = new InteractiveUserContext();
        Assert.StartsWith("S-1-5-21", u.Sid);
        Assert.Equal(CurrentSid, u.Sid); // test shell runs as the logged-on user
        Assert.True(Directory.Exists(u.ProfileDir));
        Assert.Equal(Path.Combine(u.ProfileDir, "AppData", "Local", "Temp"), u.TempDir);
        Assert.EndsWith(@"Start Menu\Programs\Startup", u.StartupDir);
        Assert.False(string.IsNullOrEmpty(u.CommonStartupDir));
    }

    [Fact]
    public void Registry_roundtrip_under_test_key_via_HKU_sid()
    {
        var reg = new WinRegistry(new FakeUserContext(_dir, CurrentSid));
        reg.Set(Hive.CurrentUser, _key, "Str", "hallo", RegistryValueKind.String);
        reg.Set(Hive.CurrentUser, _key, "Exp", @"%SystemRoot%\x.exe", RegistryValueKind.ExpandString);
        reg.Set(Hive.CurrentUser, _key, "Bin", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);

        // Written through HKU\<sid>, visible in HKCU.
        using (var k = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal("hallo", k!.GetValue("Str"));

        Assert.Equal(new[] { "Bin", "Exp", "Str" }, reg.ValueNames(Hive.CurrentUser, _key).Order());
        Assert.Equal(@"%SystemRoot%\x.exe", reg.Get(Hive.CurrentUser, _key, "Exp")); // not expanded
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, reg.Get(Hive.CurrentUser, _key, "Bin"));

        reg.Delete(Hive.CurrentUser, _key, "Str");
        reg.Delete(Hive.CurrentUser, _key, "NotThere");
        Assert.Null(reg.Get(Hive.CurrentUser, _key, "Str"));
    }

    [Fact]
    public void Registry_missing_key_reads_empty()
    {
        var reg = new WinRegistry(new FakeUserContext(_dir, CurrentSid));
        Assert.Empty(reg.ValueNames(Hive.CurrentUser, _key + @"\nope"));
        Assert.Null(reg.Get(Hive.CurrentUser, _key + @"\nope", "x"));
        reg.Delete(Hive.CurrentUser, _key + @"\nope", "x"); // no throw
    }

    [Fact]
    public void Registry_export_writes_reg_file_and_fails_for_missing_key()
    {
        var reg = new WinRegistry(new FakeUserContext(_dir, CurrentSid));
        reg.Set(Hive.CurrentUser, _key, "Str", "hallo", RegistryValueKind.String);
        var file = Path.Combine(_dir, "sub", "backup.reg");
        reg.Export(Hive.CurrentUser, _key, file);
        var text = File.ReadAllText(file);
        Assert.Contains($@"[HKEY_USERS\{CurrentSid}\{_key}]", text);
        Assert.Contains("\"Str\"=\"hallo\"", text);

        Assert.Throws<InvalidOperationException>(() =>
            reg.Export(Hive.CurrentUser, _key + @"\nope", Path.Combine(_dir, "x.reg")));
    }

    [Fact]
    public void Services_list_win32_services_with_config()
    {
        var sc = new WinServiceControl();
        var all = sc.List();
        Assert.NotEmpty(all);
        var ev = Assert.Single(all, s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(StartMode.Automatic, ev.Mode);
        Assert.True(ev.Running);
        Assert.Contains("svchost", ev.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wevtsvc.dll", ev.ServiceDll, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", ev.DisplayName[..1]); // resolved, not an indirect string

        Assert.Equal(ev, sc.Get("EventLog"));
        Assert.Null(sc.Get("Tcpip")); // kernel driver, not a service
        Assert.Null(sc.Get("WinWaechter-does-not-exist"));
    }

    [Fact]
    public void Tasks_list_reads_without_throwing()
    {
        var store = new WinTaskStore();
        var all = store.List();
        Assert.All(all, t => Assert.StartsWith(@"\", t.Path));
        if (all.Count == 0) return;
        var first = all[0];
        Assert.Equal(first.Path, store.Get(first.Path)!.Path);
        Assert.Contains("<Task", store.ExportXml(first.Path));
        Assert.Null(store.Get(@"\WinWaechter-does-not-exist"));
    }

    [Fact]
    public void FileOps_enumerates_only_old_files_and_skips_junctions()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "temp")).FullName;
        var old = Write(Path.Combine(root, "a", "old.tmp"), DateTime.UtcNow.AddDays(-3));
        Write(Path.Combine(root, "new.tmp"), DateTime.UtcNow);
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        Write(Path.Combine(outside, "victim.tmp"), DateTime.UtcNow.AddDays(-30));
        MakeJunction(Path.Combine(root, "link"), outside);

        var found = new WinFileOps().EnumerateOlderThan(root, TimeSpan.FromDays(2)).Select(f => f.FullName).ToList();
        Assert.Equal(new[] { old }, found);
        Assert.Empty(new WinFileOps().EnumerateOlderThan(Path.Combine(_dir, "missing"), TimeSpan.Zero));
    }

    [Fact]
    public void FileOps_move_creates_parents_and_exists_works()
    {
        var src = Write(Path.Combine(_dir, "src.txt"), DateTime.UtcNow);
        var dst = Path.Combine(_dir, "ablage", "1", "deep", "src.txt");
        var ops = new WinFileOps();
        ops.Move(src, dst);
        Assert.False(ops.Exists(src));
        Assert.True(ops.Exists(dst));
        Assert.True(ops.Exists(Path.GetDirectoryName(dst)!));
        Assert.Throws<IOException>(() => ops.Move(Write(src, DateTime.UtcNow), dst)); // never overwrites
    }

    [Fact]
    public void FileOps_long_path_prefix()
    {
        Assert.Equal(@"\\?\C:\a\b", WinFileOps.LongPath(@"C:\a\b"));
        Assert.Equal(@"\\?\UNC\srv\share\x", WinFileOps.LongPath(@"\\srv\share\x"));
        Assert.Equal(@"\\?\C:\a", WinFileOps.LongPath(@"\\?\C:\a"));
    }

    static string Write(string path, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
