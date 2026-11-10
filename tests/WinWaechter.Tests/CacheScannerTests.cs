using Microsoft.Win32;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class CacheScannerTests : IDisposable
{
    const string RebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    readonly string _root = Path.Combine(Path.GetTempPath(), "WinWaechterTests", Guid.NewGuid().ToString("N"));
    readonly CleanerPaths _paths;
    readonly FakeFileOps _ops = new();
    readonly FakeProcessList _procs = new();
    readonly FakeServiceControl _sc = new();
    readonly FakeRegistry _reg = new();

    public CacheScannerTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new CleanerPaths(P(@"u\AppData\Local"), P(@"u\AppData\Roaming"), P("win"), P("pd"), P("pf"), P("pf86"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string P(string rel) => Path.Combine(_root, rel);

    string Make(string path, int bytes = 1, int ageDays = 0, int? creationAgeDays = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-(creationAgeDays ?? ageDays)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    string Local(string rel) => Path.Combine(_paths.LocalAppData, rel);

    IReadOnlyList<Finding> Scan() => new CacheScanner(_ops, _paths, _procs, _sc, _reg).Scan();

    Finding Only(string title) => Assert.Single(Scan(), f => f.Title == title);

    string EdgeData => Local(@"Microsoft\Edge\User Data");

    void SeedEdge()
    {
        Make(Path.Combine(EdgeData, @"Default\Cache\a"), 3);
        Make(Path.Combine(EdgeData, @"Profile 2\Code Cache\b"), 2);
        Make(Path.Combine(EdgeData, @"ShaderCache\c"), 1);
        Make(Path.Combine(EdgeData, @"Default\Cookies"), 9);
    }

    // ---- browsers ----

    [Fact]
    public void Unreadable_browser_folder_skips_only_that_browser()
    {
        SeedEdge();
        Make(Local(@"Mozilla\Firefox\Profiles\abc.default\cache2\x"), 5);
        Make(Local(@"Google\Chrome\User Data\Default\Cache\y"), 7);
        _ops.UnreadableDirs.Add(EdgeData);
        _ops.UnreadableDirs.Add(Local(@"Mozilla\Firefox\Profiles"));

        var all = Scan();
        Assert.DoesNotContain(all, f => f.Title == "Browser-Cache Edge");
        Assert.DoesNotContain(all, f => f.Title == "Browser-Cache Firefox");
        Assert.Equal(7, Assert.Single(all, f => f.Title == "Browser-Cache Chrome").SizeBytes);
    }

    [Fact]
    public void Edge_cache_offered_with_profiles_and_shader_caches()
    {
        SeedEdge();

        var f = Only("Browser-Cache Edge");

        Assert.Equal(FindingGroups.BrowserCache, f.Group);
        Assert.Equal(FindingArea.Speicher, f.Area);
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.Equal(6, f.SizeBytes);
        Assert.Equal("cache|Browser-Cache Edge", f.Id);
        Assert.Equal("3 Dateien", f.Reasons[0]);
        Assert.Equal("cache", f.Change!.Kind);
    }

    [Fact]
    public void Custom_named_profile_with_Preferences_counts()
    {
        Make(Path.Combine(EdgeData, @"Arbeit\Preferences"));
        Make(Path.Combine(EdgeData, @"Arbeit\Cache\x"), 4);
        Make(Path.Combine(EdgeData, @"Other\Cache\y"), 8);   // no Preferences, not a profile

        Assert.Equal(4, Only("Browser-Cache Edge").SizeBytes);
    }

    [Fact]
    public void Edge_running_blocks_with_background_hint()
    {
        SeedEdge();
        _procs.List.Add(new RunningProcess("msedge.exe", Path.Combine(_paths.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe")));

        var f = Only("Browser-Cache Edge");

        Assert.Null(f.Change);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Contains("Edge läuft (auch im Hintergrund). Edge vollständig schließen und erneut prüfen.", f.Reasons);
    }

    [Fact]
    public void Failing_process_snapshot_fails_closed_as_browser_running()
    {
        SeedEdge();
        _procs.Throw = new InvalidOperationException("snapshot failed");

        var f = Only("Browser-Cache Edge");

        Assert.Null(f.Change);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public void Webview2_does_not_block_edge()
    {
        SeedEdge();
        _procs.List.Add(new RunningProcess("msedgewebview2.exe", Path.Combine(_paths.ProgramFilesX86, @"Microsoft\EdgeWebView\Application\msedgewebview2.exe")));

        Assert.NotNull(Only("Browser-Cache Edge").Change);
    }

    [Fact]
    public void Browser_counts_as_running_when_process_path_is_unreadable()
    {
        Make(Path.Combine(Local(@"Google\Chrome\User Data"), @"Default\Cache\a"));
        _procs.List.Add(new RunningProcess("chrome.exe", null));

        var f = Only("Browser-Cache Chrome");

        Assert.Null(f.Change);
        Assert.Contains("Browser ist geöffnet – zum Aufräumen schließen", f.Reasons);
    }

    [Fact]
    public void Chrome_from_other_path_does_not_block()
    {
        Make(Path.Combine(Local(@"Google\Chrome\User Data"), @"Default\Cache\a"));
        _procs.List.Add(new RunningProcess("chrome.exe", @"D:\Portable\chrome.exe"));

        Assert.NotNull(Only("Browser-Cache Chrome").Change);
    }

    [Fact]
    public void Opera_uses_local_default_and_roaming_shader_caches()
    {
        Make(Local(@"Opera Software\Opera Stable\Default\Cache\a"), 2);
        Make(Path.Combine(_paths.RoamingAppData, @"Opera Software\Opera Stable\GraphiteDawnCache\b"), 5);
        Make(Local(@"Opera Software\Opera Stable\Default\Cookies"), 50);

        var f = Only("Browser-Cache Opera");

        Assert.Equal(7, f.SizeBytes);
        Assert.Equal("2 Dateien", f.Reasons[0]);
    }

    [Fact]
    public void Opera_location_names_the_moved_folders_not_AppData()
    {
        var cache = Local(@"Opera Software\Opera Stable\Default\Cache");
        var shader = Path.Combine(_paths.RoamingAppData, @"Opera Software\Opera Stable\GraphiteDawnCache");
        Make(Path.Combine(cache, "a"), 2);
        Make(Path.Combine(shader, "b"), 5);

        var f = Only("Browser-Cache Opera");

        Assert.NotEqual(Path.GetDirectoryName(_paths.LocalAppData), f.Location);
        Assert.StartsWith(cache, f.Location);
        Assert.EndsWith(" (+1 weitere)", f.Location);
        Assert.Equal($"Zwischenspeicher in „{f.Location}“ in die Ablage verschieben", f.Change!.Description);
        Assert.Contains(cache, f.Reasons);
        Assert.Contains(shader, f.Reasons);
    }

    [Fact]
    public void Single_include_location_is_that_full_path()
    {
        var crash = Local("CrashDumps");
        Make(Path.Combine(crash, "a.dmp"), 1, 3);

        var f = Only("Absturzabbilder (Programme)");

        Assert.Equal(crash, f.Location);
        Assert.Equal($"Zwischenspeicher in „{crash}“ in die Ablage verschieben", f.Change!.Description);
    }

    [Fact]
    public void Firefox_cache2_per_profile()
    {
        Make(Local(@"Mozilla\Firefox\Profiles\abc.default\cache2\entries\e1"), 3);
        Make(Local(@"Mozilla\Firefox\Profiles\xyz.work\cache2\e2"), 4);
        Make(Local(@"Mozilla\Firefox\Profiles\abc.default\cookies.sqlite"), 99);

        Assert.Equal(7, Only("Browser-Cache Firefox").SizeBytes);
    }

    // ---- Windows Update ----

    string WuDir => Path.Combine(_paths.SystemRoot, @"SoftwareDistribution\Download");

    [Fact]
    public void WU_downloads_offered_when_idle_and_older_than_7_days()
    {
        Make(Path.Combine(WuDir, "old.cab"), 10, ageDays: 10);
        Make(Path.Combine(WuDir, "fresh-created.cab"), 20, ageDays: 10, creationAgeDays: 1);   // newer timestamp wins
        Make(Path.Combine(WuDir, "new.cab"), 30, ageDays: 1);

        var f = Only("Windows-Update-Downloads");

        Assert.Equal(FindingGroups.WindowsRemnants, f.Group);
        Assert.NotNull(f.Change);
        Assert.Equal(10, f.SizeBytes);
        Assert.Equal("1 Dateien älter als 7 Tage", f.Reasons[0]);
    }

    [Fact]
    public void WU_blocked_when_reboot_pending()
    {
        Make(Path.Combine(WuDir, "old.cab"), 10, ageDays: 10);
        _reg.SeedKey(Hive.LocalMachine, RebootKey);

        var f = Only("Windows-Update-Downloads");

        Assert.Null(f.Change);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.Contains("Ein Neustart nach Updates steht aus – erst neu starten", f.Reasons);
    }

    [Fact]
    public void WU_blocked_by_pending_rename()
    {
        Make(Path.Combine(WuDir, "old.cab"), 10, ageDays: 10);
        _reg.Seed(Hive.LocalMachine, SessionManager, "PendingFileRenameOperations", new[] { @"\??\C:\x", "" }, RegistryValueKind.MultiString);

        var f = Only("Windows-Update-Downloads");

        Assert.Null(f.Change);
        Assert.Contains("Ein Neustart nach Updates steht aus – erst neu starten", f.Reasons);
    }

    [Fact]
    public void WU_not_blocked_by_empty_pending_rename()
    {
        Make(Path.Combine(WuDir, "old.cab"), 10, ageDays: 10);
        _reg.Seed(Hive.LocalMachine, SessionManager, "PendingFileRenameOperations", Array.Empty<string>(), RegistryValueKind.MultiString);

        Assert.NotNull(Only("Windows-Update-Downloads").Change);
    }

    [Fact]
    public void WU_blocked_when_trustedinstaller_runs_or_wuauserv_busy()
    {
        Make(Path.Combine(WuDir, "old.cab"), 10, ageDays: 10);

        _sc.Add("TrustedInstaller", running: true);
        var ti = Only("Windows-Update-Downloads");
        Assert.Null(ti.Change);
        Assert.Contains("Windows Update arbeitet gerade", ti.Reasons);
        _sc.Remove("TrustedInstaller");

        _sc.Add("wuauserv", running: true);
        var recent = Make(Path.Combine(WuDir, "recent.cab"), 1, ageDays: 0);
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddMinutes(-10));
        var busy = Only("Windows-Update-Downloads");
        Assert.Null(busy.Change);
        Assert.Contains("Windows Update arbeitet gerade", busy.Reasons);

        File.Delete(recent);
        Assert.NotNull(Only("Windows-Update-Downloads").Change);   // wuauserv alone, all files old: fine
    }

    // ---- dumps and reports ----

    [Fact]
    public void Dumps_and_wer_targets_and_memory_dmp_single()
    {
        Make(Local(@"CrashDumps\a.dmp"), 1, 3);
        Make(Local(@"CrashDumps\new.dmp"), 100, 0);
        Make(Path.Combine(_paths.SystemRoot, @"Minidump\m.dmp"), 2, 3);
        Make(Path.Combine(_paths.SystemRoot, @"LiveKernelReports\k.dmp"), 4, 3);
        Make(Path.Combine(_paths.ProgramData, @"Microsoft\Windows\WER\ReportQueue\r"), 8, 3);
        Make(Local(@"Microsoft\Windows\WER\ReportArchive\u"), 16, 3);
        Make(Path.Combine(_paths.SystemRoot, "MEMORY.DMP"), 32, 3);

        var all = Scan();

        Assert.All(all, f => Assert.Equal(FindingGroups.WindowsRemnants, f.Group));
        Assert.Equal(1, all.Single(f => f.Title == "Absturzabbilder (Programme)").SizeBytes);   // fresh dump not counted
        var win = all.Single(f => f.Title == "Absturzabbilder (Windows)");
        Assert.Equal(6, win.SizeBytes);
        Assert.Equal(Path.Combine(_paths.SystemRoot, "Minidump") + " (+1 weitere)", win.Location);
        Assert.Equal($"Zwischenspeicher in „{win.Location}“ in die Ablage verschieben", win.Change!.Description);
        Assert.Contains(Path.Combine(_paths.SystemRoot, "LiveKernelReports"), win.Reasons);
        Assert.Equal(8, all.Single(f => f.Title == "Fehlerberichte (Windows)").SizeBytes);
        Assert.Equal(16, all.Single(f => f.Title == "Fehlerberichte (Benutzer)").SizeBytes);
        var mem = all.Single(f => f.Title == "Speicherabbild (MEMORY.DMP)");
        Assert.Equal("cache", mem.Change!.Kind);
        Assert.Equal(32, mem.SizeBytes);
        Assert.Equal(Path.Combine(_paths.SystemRoot, "MEMORY.DMP"), mem.Location);
    }

    [Fact]
    public void Fresh_memory_dmp_is_not_offered()
    {
        Make(Path.Combine(_paths.SystemRoot, "MEMORY.DMP"), 32, 1);

        Assert.Empty(Scan());
    }

    [Fact]
    public void Missing_folders_give_no_findings()
    {
        Assert.Empty(Scan());
    }

    // ---- installers ----

    [Fact]
    public void Old_installers_one_per_file_never_preselected()
    {
        var dl = P("dl");
        var setup = Make(Path.Combine(dl, "setup.exe"), 5, 40);
        Make(Path.Combine(dl, "tool.msi"), 6, 31);
        Make(Path.Combine(dl, "new.exe"), 7, 5);
        Make(Path.Combine(dl, "doc.pdf"), 8, 90);
        Make(Path.Combine(dl, @"sub\old.exe"), 9, 90);
        var sig = new FakeSignatures().Signed(setup, "Foo GmbH");
        sig.Default = new SignatureInfo(SignatureState.Unsigned, null, false);

        var found = new InstallerScanner(_ops, sig, dl).Scan();

        Assert.Equal(2, found.Count);
        Assert.All(found, f =>
        {
            Assert.False(f.Preselected);
            Assert.Equal(Rating.Info, f.Rating);
            Assert.Equal(FindingGroups.OldInstallers, f.Group);
            Assert.Equal("file", f.Change!.Kind);
        });
        var s = found.Single(f => f.Title == "setup.exe");
        Assert.Equal("Foo GmbH", s.Publisher);
        Assert.Equal(5, s.SizeBytes);
        Assert.Equal($"installer|{setup}", s.Id);
        Assert.Contains("Signatur: gültig, Herausgeber Foo GmbH", s.Reasons);
        Assert.StartsWith("Zuletzt geändert am ", s.Reasons[0]);
        Assert.Contains("Signatur: nicht signiert", found.Single(f => f.Title == "tool.msi").Reasons);
    }

    [Fact]
    public void Installer_signature_texts_cover_invalid_and_unknown()
    {
        var dl = P("dl");
        var a = Make(Path.Combine(dl, "a.exe"), 1, 40);
        Make(Path.Combine(dl, "b.exe"), 1, 40);
        var sig = new FakeSignatures().Set(a, new SignatureInfo(SignatureState.Invalid, null, false));

        var found = new InstallerScanner(_ops, sig, dl).Scan();

        Assert.Contains("Signatur: ungültig", found.Single(f => f.Title == "a.exe").Reasons);
        Assert.Contains("Signatur: nicht prüfbar", found.Single(f => f.Title == "b.exe").Reasons);
    }
}
