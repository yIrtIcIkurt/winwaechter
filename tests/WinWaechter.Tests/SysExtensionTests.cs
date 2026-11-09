using Microsoft.Win32;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SysExtensionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "WinWaechterTests", Guid.NewGuid().ToString("N"));
    readonly WinFileOps _ops = new();

    public SysExtensionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string Write(string path, int bytes, int ageDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void WinRegistry_reads_kind_and_subkeys_read_only()
    {
        var reg = new WinRegistry(new FakeUserContext(_root));
        Assert.Equal(RegistryValueKind.String,
            reg.GetKind(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild"));
        Assert.Null(reg.GetKind(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "NoSuchValue_ww"));
        Assert.Null(reg.GetKind(Hive.LocalMachine, @"SOFTWARE\NoSuchKey_ww", "x"));
        Assert.Contains(reg.SubKeyNames(Hive.LocalMachine, @"SOFTWARE\Microsoft"), n => n.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(reg.SubKeyNames(Hive.LocalMachine, @"SOFTWARE\NoSuchKey_ww"));
    }

    [Fact]
    public void FakeRegistry_GetKind_and_SubKeyNames()
    {
        var reg = new FakeRegistry().Seed(Hive.CurrentUser, @"A\B", "v", 1, RegistryValueKind.DWord);
        reg.SeedKey(Hive.CurrentUser, @"A\C");
        Assert.Equal(RegistryValueKind.DWord, reg.GetKind(Hive.CurrentUser, @"A\B", "v"));
        Assert.Null(reg.GetKind(Hive.CurrentUser, @"A\B", "other"));
        Assert.Equal(new[] { "B", "C" }, reg.SubKeyNames(Hive.CurrentUser, "A").OrderBy(x => x).ToArray());
        Assert.Empty(reg.SubKeyNames(Hive.CurrentUser, "Nope"));
    }

    [Fact]
    public void InspectFolder_reports_size_latest_write_and_binaries()
    {
        var folder = Path.Combine(_root, "f");
        Write(Path.Combine(folder, "x", "a.dll"), 3, 40);
        Write(Path.Combine(folder, "b.txt"), 2, 35);
        var old = DateTime.UtcNow.AddDays(-40);
        Directory.SetLastWriteTimeUtc(Path.Combine(folder, "x"), old);
        Directory.SetLastWriteTimeUtc(folder, old);

        var f = _ops.InspectFolder(folder);
        Assert.NotNull(f);
        Assert.Equal(5, f.Bytes);
        Assert.Equal(2, f.Files);
        Assert.True(f.HasBinaries);
        Assert.False(f.HasReparsePoints);
        Assert.InRange((f.LatestWriteUtc - DateTime.UtcNow.AddDays(-35)).TotalSeconds, -60, 60);
        Assert.Null(_ops.InspectFolder(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void InspectFolder_flags_inner_junction_and_returns_null_for_junction_root()
    {
        var folder = Path.Combine(_root, "f");
        Write(Path.Combine(folder, "a.txt"), 4, 1);
        var outside = Path.Combine(_root, "outside");
        Write(Path.Combine(outside, "big.bin"), 100, 1);
        var link = Path.Combine(folder, "link");
        SysSmokeTests.MakeJunction(link, outside);

        var f = _ops.InspectFolder(folder);
        Assert.NotNull(f);
        Assert.True(f.HasReparsePoints);
        Assert.Equal(4, f.Bytes);
        Assert.Null(_ops.InspectFolder(link));
    }

    [Fact]
    public void MoveDirectory_moves_whole_tree_and_refuses_existing_target_and_junction()
    {
        var src = Path.Combine(_root, "src");
        Write(Path.Combine(src, "n", "f.txt"), 1, 1);
        var dst = Path.Combine(_root, "dst", "sub", "src");
        _ops.MoveDirectory(src, dst);
        Assert.True(File.Exists(Path.Combine(dst, "n", "f.txt")));
        Assert.False(Directory.Exists(src));

        var src2 = Path.Combine(_root, "src2");
        Write(Path.Combine(src2, "g.txt"), 1, 1);
        Assert.Throws<IOException>(() => _ops.MoveDirectory(src2, dst));
        Assert.True(Directory.Exists(src2));

        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "junc");
        SysSmokeTests.MakeJunction(junction, target);
        Assert.Throws<IOException>(() => _ops.MoveDirectory(junction, Path.Combine(_root, "dst2", "junc")));
        Assert.True(Directory.Exists(junction));
    }

    [Fact]
    public void IsVolumeReady_false_for_unc_and_true_for_temp()
    {
        Assert.False(_ops.IsVolumeReady(@"\\server\share\x.exe"));
        Assert.True(_ops.IsVolumeReady(_root));
    }

    [Fact]
    public void EnumerateTopLevelDirs_includes_junction_dirs_and_handles_missing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d1"));
        var target = Directory.CreateDirectory(Path.Combine(_root, "t")).FullName;
        SysSmokeTests.MakeJunction(Path.Combine(_root, "j"), target);
        var names = _ops.EnumerateTopLevelDirs(_root).Select(d => d.Name).ToList();
        Assert.Contains("d1", names);
        Assert.Contains("j", names);
        Assert.Empty(_ops.EnumerateTopLevelDirs(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void ProgramPresence_cases()
    {
        var files = new FakeFileOps();
        Assert.Equal(Presence.Unknown, ProgramPresence.Check(null, files));
        Assert.Equal(Presence.Unknown, ProgramPresence.Check("", files));
        Assert.Equal(Presence.Unknown, ProgramPresence.Check("relative.exe", files));
        Assert.Equal(Presence.Unknown, ProgramPresence.Check(@"\\server\share\a.exe", files));

        var present = Write(Path.Combine(_root, "p.exe"), 1, 1);
        Assert.Equal(Presence.Present, ProgramPresence.Check(present, files));
        Assert.Equal(Presence.Missing, ProgramPresence.Check(Path.Combine(_root, "gone.exe"), files));
        Assert.Equal(Presence.Missing, ProgramPresence.Check(@"%TEMP%\nope_ww.exe", files));

        files.NotReady.Add(Path.GetPathRoot(present)!);
        Assert.Equal(Presence.Unknown, ProgramPresence.Check(present, files));
    }

    [Fact]
    public void DownloadsFolder_resolves_user_shell_folder_with_profile_of_interactive_user()
    {
        var u = new FakeUserContext(_root);
        var reg = new FakeRegistry().Seed(Hive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
            DownloadsFolder.KnownFolderId, @"%USERPROFILE%\Daten\Downloads", RegistryValueKind.ExpandString);
        Assert.Equal(Path.Combine(u.ProfileDir, "Daten", "Downloads"), DownloadsFolder.Resolve(reg, u));
        Assert.Equal(Path.Combine(u.ProfileDir, "Downloads"), DownloadsFolder.Resolve(new FakeRegistry(), u));
    }

    [Fact]
    public void CleanerPaths_derive_from_profile()
    {
        var p = CleanerPaths.For(new FakeUserContext(_root));
        Assert.Equal(Path.Combine(_root, "AppData", "Local"), p.LocalAppData);
        Assert.Equal(Path.Combine(_root, "AppData", "Roaming"), p.RoamingAppData);
        Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), p.SystemRoot);
    }

    [Fact]
    public void WinProcessList_snapshot_contains_own_process()
    {
        var snap = new WinProcessList().Snapshot();
        Assert.Contains(snap, p => string.Equals(p.ExePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                                   && p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }
}
