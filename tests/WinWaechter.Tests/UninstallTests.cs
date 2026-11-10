using System.Diagnostics;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class UninstallTests : IDisposable
{
    const string Guid = "{11111111-2222-3333-4444-555555555555}";

    readonly string _tmp = Path.Combine(Path.GetTempPath(), "ww-uninst-" + System.Guid.NewGuid().ToString("N"));
    readonly CleanerPaths _paths;
    readonly FakePathTrust _trust = new();

    public UninstallTests()
    {
        Directory.CreateDirectory(_tmp);
        _paths = new CleanerPaths(
            LocalAppData: Path.Combine(_tmp, "profile", "AppData", "Local"),
            RoamingAppData: Path.Combine(_tmp, "profile", "AppData", "Roaming"),
            SystemRoot: Path.Combine(_tmp, "Windows"),
            ProgramData: Path.Combine(_tmp, "ProgramData"),
            ProgramFiles: Path.Combine(_tmp, "Program Files"),
            ProgramFilesX86: Path.Combine(_tmp, "Program Files (x86)"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    static InstalledProgram Prog(string? uninstall, Hive hive = Hive.LocalMachine, string key = "Foo",
        bool msi = false) =>
        new(hive, hive == Hive.CurrentUser ? UninstallRoots.User : UninstallRoots.Native, key, "Foo", "Foo GmbH",
            "1.0", null, null, uninstall, null, null, msi, ProgramFlag.None);

    UninstallPlan Plan(InstalledProgram p) => UninstallPlanner.Plan(p, _paths, File.Exists, _trust);

    [Fact]
    public void Parse_quoted_unquoted_and_args()
    {
        var q = UninstallCommandParser.Parse("\"C:\\P F\\u.exe\" /S", File.Exists);
        Assert.Equal(new UninstallCommand(@"C:\P F\u.exe", "/S"), q);

        var unins = Touch(Path.Combine(_tmp, "Foo Bar", "unins000.exe"));
        var u = UninstallCommandParser.Parse(unins + " /SILENT", File.Exists);
        Assert.Equal(new UninstallCommand(unins, "/SILENT"), u);

        var ab = Touch(Path.Combine(_tmp, "a.exe b.exe"));
        var l = UninstallCommandParser.Parse(Path.Combine(_tmp, "a.exe b.exe x"), File.Exists);
        Assert.Equal(new UninstallCommand(ab, "x"), l);

        Assert.Null(UninstallCommandParser.Parse("relative.exe", File.Exists));
        Assert.Null(UninstallCommandParser.Parse("C:\\x\\setup.bat", File.Exists));
        // Never network or stream paths.
        Assert.Null(UninstallCommandParser.Parse("\"\\\\server\\share\\u.exe\"", _ => throw new Exception("probed")));
        Assert.Null(UninstallCommandParser.Parse("\\\\server\\share\\u.exe /S", _ => throw new Exception("probed")));
        Assert.Null(UninstallCommandParser.Parse("\"C:\\x\\a.txt:b.exe\"", File.Exists));
    }

    [Fact]
    public void Msi_product_code_from_key_or_string()
    {
        Assert.Equal(Guid, UninstallCommandParser.MsiProductCode(Prog(null, key: Guid, msi: true)));
        Assert.Equal(Guid, UninstallCommandParser.MsiProductCode(Prog("MsiExec.exe /I" + Guid)));
        Assert.Null(UninstallCommandParser.MsiProductCode(Prog("MsiExec.exe /I{bad}")));
        Assert.Equal(Guid, UninstallCommandParser.MsiProductCode(Prog(@"""C:\Windows\System32\MsiExec.exe"" /X" + Guid)));
        // Only the bare /I{GUID} or /X{GUID} form counts; extra switches do not.
        Assert.Null(UninstallCommandParser.MsiProductCode(Prog("MsiExec.exe /X" + Guid + " /qn")));
        Assert.Null(UninstallCommandParser.MsiProductCode(Prog("MsiExec.exe /i x.msi")));
    }

    [Fact]
    public void Msiexec_without_validated_guid_opens_settings()
    {
        var msiexec = Touch(Path.Combine(_paths.SystemRoot, "System32", "msiexec.exe"));
        foreach (var s in new[]
                 {
                     "MsiExec.exe /i x.msi", "MsiExec.exe /X" + Guid + " /qn", "MsiExec.exe /X",
                     "\"" + msiexec + "\" /i x.msi", "\"" + msiexec + "\" /y x.dll", "\"" + msiexec + "\"",
                     msiexec + " /X" + Guid + " REBOOT=R",
                 })
        {
            var plan = Plan(Prog(s));
            Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
            Assert.Null(plan.Command);
        }
        // With WindowsInstaller=1 the GUID comes from the key name; the string's arguments are never used.
        var keyed = Plan(Prog("MsiExec.exe /i x.msi /qn", key: Guid, msi: true));
        Assert.Equal(UninstallRoute.Launch, keyed.Route);
        Assert.Equal("/X" + Guid, keyed.Command!.Arguments);
    }

    [Fact]
    public void Msi_entry_launches_msiexec_with_X()
    {
        var plan = Plan(Prog("MsiExec.exe /I" + Guid));
        Assert.Equal(UninstallRoute.Launch, plan.Route);
        Assert.Equal(Path.Combine(_paths.SystemRoot, "System32", "msiexec.exe"), plan.Command!.Exe);
        Assert.Equal("/X" + Guid, plan.Command.Arguments);

        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("MsiExec.exe /I" + Guid, Hive.CurrentUser)).Route);
    }

    [Fact]
    public void Hklm_exe_under_program_files_launches()
    {
        var exe = Touch(Path.Combine(_paths.ProgramFiles, "Foo", "uninst.exe"));
        var plan = Plan(Prog("\"" + exe + "\" /S"));
        Assert.Equal(UninstallRoute.Launch, plan.Route);
        Assert.Equal(new UninstallCommand(exe, "/S"), plan.Command);
        Assert.Equal("", plan.Reason);
        Assert.False(plan.UninstallerMissing);
    }

    [Fact]
    public void Package_cache_allowed_only_under_programdata()
    {
        var ok = Touch(Path.Combine(_paths.ProgramData, "Package Cache", "{x}", "setup.exe"));
        Assert.Equal(UninstallRoute.Launch, Plan(Prog("\"" + ok + "\" /uninstall")).Route);

        var bad = Touch(Path.Combine(_paths.LocalAppData, "Package Cache", "{x}", "setup.exe"));
        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("\"" + bad + "\" /uninstall")).Route);
    }

    [Fact]
    public void Hklm_entry_with_appdata_uninstaller_opens_settings()
    {
        var exe = Touch(Path.Combine(_paths.LocalAppData, "Foo", "uninst.exe"));
        var plan = Plan(Prog("\"" + exe + "\""));
        Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
        Assert.False(plan.UninstallerMissing);
        Assert.Contains("Benutzerkonto", plan.Reason);
    }

    [Fact]
    public void Hkcu_entry_opens_settings_even_under_program_files()
    {
        var exe = Touch(Path.Combine(_paths.ProgramFiles, "Foo", "uninst.exe"));
        var plan = Plan(Prog("\"" + exe + "\"", Hive.CurrentUser));
        Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
        Assert.False(plan.UninstallerMissing);
    }

    [Fact]
    public void Path_escaping_program_files_opens_settings()
    {
        Touch(Path.Combine(_paths.LocalAppData, "Foo", "uninst.exe"));
        var sneaky = Path.Combine(_paths.ProgramFiles, "..", "profile", "AppData", "Local", "Foo", "uninst.exe");
        Assert.True(File.Exists(sneaky));
        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("\"" + sneaky + "\"")).Route);
        // A sibling folder that only shares the prefix is not "under" Program Files.
        var sibling = Touch(_paths.ProgramFiles + "Evil\\uninst.exe");
        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("\"" + sibling + "\"")).Route);
    }

    [Fact]
    public void Refused_hosts_open_settings()
    {
        var cmd = Touch(Path.Combine(_paths.SystemRoot, "System32", "cmd.exe"));
        var rundll = Touch(Path.Combine(_paths.SystemRoot, "System32", "rundll32.exe"));
        foreach (var s in new[]
                 {
                     "cmd.exe /c del x", "rundll32.exe x.dll,Uninstall", "powershell -File u.ps1", "\"C:\\x\\u.cmd\"",
                     "\"" + cmd + "\" /c del x", "\"" + rundll + "\" x.dll,Uninstall",
                 })
        {
            var plan = Plan(Prog(s));
            Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
            Assert.Null(plan.Command);
            Assert.Equal("Der Deinstallationsbefehl kann nicht sicher gestartet werden.", plan.Reason);
        }
    }

    [Fact]
    public void Missing_uninstaller_is_flagged()
    {
        var plan = Plan(Prog("\"" + Path.Combine(_paths.ProgramFiles, "Gone", "uninst.exe") + "\""));
        Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
        Assert.True(plan.UninstallerMissing);
        Assert.Equal("Das Deinstallationsprogramm fehlt.", plan.Reason);
    }

    [Fact]
    public void Launch_plans_carry_the_install_location()
    {
        var exe = Touch(Path.Combine(_paths.ProgramFiles, "Foo", "uninst.exe"));
        var folder = Path.Combine(_paths.ProgramFiles, "Foo");
        Assert.Equal(folder, Plan(Prog("\"" + exe + "\"") with { InstallLocation = folder }).InstallLocation);
        Assert.Equal(folder, Plan(Prog("MsiExec.exe /X" + Guid) with { InstallLocation = folder }).InstallLocation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_uninstall_string_counts_as_missing_uninstaller(string? uninstall)
    {
        var plan = Plan(Prog(uninstall));
        Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
        Assert.Null(plan.Command);
        Assert.True(plan.UninstallerMissing);
        Assert.Equal(UninstallPlanner.MissingReason, plan.Reason);
        // an MSI product code still takes the msiexec route
        Assert.Equal(UninstallRoute.Launch, Plan(Prog(uninstall, key: Guid, msi: true)).Route);
    }

    [Fact]
    public void Writable_uninstaller_or_folder_opens_settings()
    {
        var exe = Touch(Path.Combine(_paths.ProgramFiles, "Foo", "Bin", "uninst.exe"));
        var prog = Prog("\"" + exe + "\"");
        Assert.Equal(UninstallRoute.Launch, Plan(prog).Route);
        // Exe, every folder up to the root, and the root itself are checked; nothing above the root.
        Assert.Equal(
            [exe, Path.GetDirectoryName(exe)!, Path.Combine(_paths.ProgramFiles, "Foo"), _paths.ProgramFiles],
            _trust.Calls);

        foreach (var untrusted in new[]
                 {
                     exe, Path.GetDirectoryName(exe)!, Path.Combine(_paths.ProgramFiles, "Foo"), _paths.ProgramFiles,
                 })
        {
            _trust.Untrusted.Clear();
            _trust.Untrusted.Add(untrusted);
            var plan = Plan(prog);
            Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
            Assert.Equal(UninstallPlanner.UserWritableReason, plan.Reason);
            Assert.False(plan.UninstallerMissing);
        }

        // A user-created Package Cache under ProgramData is caught the same way.
        var cache = Touch(Path.Combine(_paths.ProgramData, "Package Cache", "{x}", "setup.exe"));
        _trust.Untrusted.Clear();
        _trust.Untrusted.Add(Path.Combine(_paths.ProgramData, "Package Cache"));
        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("\"" + cache + "\" /uninstall")).Route);
    }

    [Fact]
    public void Msi_launch_requires_trusted_msiexec()
    {
        var msiexec = Path.Combine(_paths.SystemRoot, "System32", "msiexec.exe");
        _trust.Untrusted.Add(msiexec);
        Assert.Equal(UninstallRoute.OpenSettings, Plan(Prog("MsiExec.exe /X" + Guid)).Route);
    }

    [Fact]
    public void Only_msiexec_may_run_from_system_root()
    {
        var exe = Touch(Path.Combine(_paths.SystemRoot, "System32", "foo", "uninst.exe"));
        var plan = Plan(Prog("\"" + exe + "\" /S"));
        Assert.Equal(UninstallRoute.OpenSettings, plan.Route);
        Assert.Null(plan.Command);
        Assert.Equal(UninstallPlanner.UnsafeReason, plan.Reason);
        Assert.Empty(_trust.Calls);
        // The validated MSI route still uses System32\msiexec.exe.
        Assert.Equal(UninstallRoute.Launch, Plan(Prog("MsiExec.exe /X" + Guid)).Route);
    }

    static bool Sddl(string sddl)
    {
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(sddl);
        return WinPathTrust.Evaluate(
            sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier,
            sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)));
    }

    [Theory]
    [InlineData("O:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;0x1200a9;;;BU)", true)]
    [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;0x1200a9;;;BU)", true)]
    [InlineData("O:SYD:(A;;FA;;;SY)(A;;0x1200a9;;;WD)", true)]
    [InlineData("O:BAD:(A;;GW;;;BU)", false)]
    [InlineData("O:BAD:(A;OICIIO;GA;;;BU)", true)]                      // inherit-only: not on this object
    [InlineData("O:S-1-5-21-1000-1000-1000-1001D:(A;;FA;;;BA)", false)] // owned by a user
    [InlineData("D:(A;;FA;;;BA)", false)]                               // no owner
    [InlineData("O:BAD:(A;;FA;;;S-1-5-113)", false)]                    // Local account
    [InlineData("O:BAD:(A;OICIIO;GA;;;CO)", true)]                      // CREATOR OWNER, inherit-only
    [InlineData("O:BAD:(A;;GA;;;CO)", false)]                           // CREATOR OWNER on the object itself
    [InlineData("O:BAD:(A;;0x1200a9;;;BU)(A;;0x4;;;BU)", false)]        // AppendData / create subfolder
    [InlineData("O:BAD:(A;;WD;;;S-1-5-21-1-2-3-1001)", false)]          // WRITE_DAC for any other SID
    [InlineData("O:BAD:(A;;WO;;;AU)", false)]                           // WRITE_OWNER
    [InlineData("O:BAD:(A;;SD;;;IU)", false)]                           // DELETE
    [InlineData("O:BAD:(A;;GA;;;S-1-15-2-1)", false)]                   // app packages, GENERIC_ALL
    [InlineData("O:BAD:(D;;FA;;;BU)(A;;FA;;;BA)", true)]                // deny entries never grant
    public void Trust_evaluation_is_an_allow_list(string sddl, bool trusted) => Assert.Equal(trusted, Sddl(sddl));

    [Fact]
    public void WinPathTrust_refuses_reparse_points()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var link = Path.Combine(_tmp, "link");
        var psi = new ProcessStartInfo(Path.Combine(target, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        using (var p = Process.Start(psi)!) p.WaitForExit();
        try
        {
            Assert.True(Directory.Exists(link));
            Assert.True(new WinPathTrust().IsAdminOnly(target));
            // Same ACL as the target, but the link itself is refused (the planner checks every folder of the chain).
            Assert.False(new WinPathTrust().IsAdminOnly(link));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
        }
    }

    [Fact]
    public void WinPathTrust_reads_real_acls()
    {
        var trust = new WinPathTrust();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(trust.IsAdminOnly(Path.Combine(win, "System32", "msiexec.exe")));
        Assert.True(trust.IsAdminOnly(Path.Combine(win, "System32")));
        Assert.True(trust.IsAdminOnly(win));
        Assert.True(trust.IsAdminOnly(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(trust.IsAdminOnly(Path.Combine(win, "Temp")));      // Users may create files there
        // Users may create folders in the ProgramData root, so a missing "Package Cache" could be user-made.
        Assert.False(trust.IsAdminOnly(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        Assert.False(trust.IsAdminOnly(Touch(Path.Combine(_tmp, "u.exe"))));
        Assert.False(trust.IsAdminOnly(_tmp));
        Assert.False(trust.IsAdminOnly(Path.Combine(_tmp, "missing.exe")));
    }

    [Fact]
    public void Exit_codes()
    {
        Assert.True(UninstallExit.Interpret(1602).Cancelled);
        Assert.True(UninstallExit.Interpret(3010).RestartRequired);
        Assert.True(UninstallExit.Interpret(1641).RestartRequired);
        var ok = UninstallExit.Interpret(0);
        Assert.False(ok.Cancelled);
        Assert.False(ok.RestartRequired);
        Assert.Equal(0, ok.ExitCode);
    }

    [Fact]
    public void SystemLinks_allow_only_fixed_list()
    {
        foreach (var u in new[]
                 {
                     SystemLinks.AppsFeatures, SystemLinks.WindowsUpdate, SystemLinks.RemoteDesktop,
                     SystemLinks.DeviceEncryption, SystemLinks.DefenderThreat, SystemLinks.DefenderAppBrowser,
                 })
            Assert.True(SystemLinks.IsAllowed(u));
        Assert.Equal(6, SystemLinks.Allowed.Count);
        // the elevated process never hands a web address to the shell (the browser could start elevated)
        Assert.All(SystemLinks.Allowed, u =>
            Assert.True(u.StartsWith("ms-settings:", StringComparison.Ordinal) || u.StartsWith("windowsdefender:", StringComparison.Ordinal), u));
        Assert.False(SystemLinks.IsAllowed("https://github.com/yIrtIcIkurt/winwaechter"));
        Assert.False(SystemLinks.IsAllowed("https://github.com/yIrtIcIkurt/winwaechter/releases"));
        Assert.True(SystemLinks.IsAllowed("MS-SETTINGS:APPSFEATURES"));
        Assert.False(SystemLinks.IsAllowed("ms-settings:appsfeatures "));
        Assert.False(SystemLinks.IsAllowed("https://evil.com"));
        Assert.False(SystemLinks.IsAllowed("ms-settings:privacy"));
        Assert.False(SystemLinks.IsAllowed("file:///C:/x.exe"));
        Assert.False(SystemLinks.IsAllowed(null));
    }

    [Fact]
    public async Task Launcher_waits_for_child_processes_and_returns_exit_code()
    {
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var sw = Stopwatch.StartNew();
        var outcome = await new JobUninstallLauncher().RunAsync(new UninstallCommand(Path.Combine(sys32, "cmd.exe"),
            "/c start \"\" /b \"%SystemRoot%\\System32\\ping.exe\" -n 3 127.0.0.1 >nul & exit 7"));
        sw.Stop();
        Assert.Equal(7, outcome.ExitCode);
        Assert.False(outcome.ChildrenStillRunning);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5), $"returned after {sw.Elapsed}");
    }

    [Fact]
    public async Task Launcher_stops_waiting_for_children_after_the_grace_period()
    {
        // ping runs about 5 s after cmd (the root) has ended; with a 1 s grace the launcher returns early
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var sw = Stopwatch.StartNew();
        var outcome = await new JobUninstallLauncher(TimeSpan.FromSeconds(1)).RunAsync(new UninstallCommand(
            Path.Combine(sys32, "cmd.exe"), "/c start \"\" /b \"%SystemRoot%\\System32\\ping.exe\" -n 6 127.0.0.1 >nul & exit 3010"));
        sw.Stop();
        Assert.Equal(3010, outcome.ExitCode);
        Assert.True(outcome.RestartRequired);
        Assert.True(outcome.ChildrenStillRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"returned after {sw.Elapsed}");
    }

    [Fact]
    public async Task Job_wait_gives_leftover_children_a_grace_period_only_after_the_root_ended()
    {
        var poll = TimeSpan.FromMilliseconds(10);
        var grace = TimeSpan.FromMilliseconds(300);

        const string Browser = @"C:\Program Files\Browser\browser.exe";
        static IReadOnlyList<string?> Running(int n, string? path) => Enumerable.Repeat(path, n).ToList();
        static bool NeverUninstaller(string? _) => false;

        // root ended, a browser keeps running forever: back after the grace period, flag set
        var sw = Stopwatch.StartNew();
        Assert.True(await JobUninstallLauncher.WaitForJobAsync(() => true, () => Running(1, Browser), NeverUninstaller, grace, poll));
        Assert.True(sw.Elapsed >= grace && sw.Elapsed < TimeSpan.FromSeconds(5), $"returned after {sw.Elapsed}");

        // root still running past the grace period, then everything ends: waited for all, no flag
        var polls = 0;
        Assert.False(await JobUninstallLauncher.WaitForJobAsync(() => false, () => Running(++polls < 60 ? 2 : 0, Browser),
            NeverUninstaller, grace, poll));
        Assert.Equal(60, polls);

        // root ended, child ends within the grace period: no flag
        var left = 5;
        Assert.False(await JobUninstallLauncher.WaitForJobAsync(() => true, () => Running(Math.Max(0, left--), Browser),
            NeverUninstaller, grace, poll));
    }

    [Fact]
    public async Task Job_wait_keeps_waiting_for_uninstaller_copies_past_the_grace_period()
    {
        var poll = TimeSpan.FromMilliseconds(10);
        var grace = TimeSpan.FromMilliseconds(100);
        var dirs = JobUninstallLauncher.UninstallerDirs(new UninstallCommand(@"C:\Program Files\Foo\uninst.exe", ""), null);
        bool IsUninstaller(string? p) => JobUninstallLauncher.IsUninstallerProcess(p, dirs);
        var temp = Path.Combine(Path.GetTempPath(), "~nsu.tmp", "Au_.exe");

        // root ended, the real uninstaller runs from %TEMP%: still waiting long after the grace period
        foreach (var copy in new string?[] { temp, @"C:\Program Files\Foo\_iu14D2N.tmp", null })
        {
            var sw = Stopwatch.StartNew();
            var polls = 0;
            var flag = await JobUninstallLauncher.WaitForJobAsync(() => true, () =>
            {
                polls++;
                // the copy runs for 5 grace periods, then a survey browser stays open
                if (sw.Elapsed < grace * 5) return [copy, @"C:\Program Files\Browser\browser.exe"];
                return [@"C:\Program Files\Browser\browser.exe"];
            }, IsUninstaller, grace, poll);
            Assert.True(flag, copy);
            Assert.True(sw.Elapsed >= grace * 6, $"{copy}: returned after {sw.Elapsed}");
        }
    }

    [Fact]
    public void Uninstaller_processes_are_temp_copies_its_own_folders_or_unreadable()
    {
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dirs = JobUninstallLauncher.UninstallerDirs(new UninstallCommand(Path.Combine(pf, "Foo", "bin", "uninst.exe"), ""),
            "\"" + Path.Combine(pf, "Foo") + "\\\"");
        bool Is(string? p) => JobUninstallLauncher.IsUninstallerProcess(p, dirs);

        Assert.True(Is(null));
        Assert.True(Is(""));
        Assert.True(Is(Path.Combine(Path.GetTempPath(), "Au_.exe")));
        Assert.True(Is(Path.Combine(sysRoot, "Temp", "x", "setup.exe")));
        Assert.True(Is(@"D:\Users\someone\AppData\Local\Temp\_iu14D2N.tmp"));
        Assert.True(Is(Path.Combine(pf, "Foo", "helper.exe")));
        Assert.True(Is(Path.Combine(pf, "FOO", "bin", "uninst.exe")));
        Assert.False(Is(Path.Combine(pf, "Foobar", "x.exe")));
        Assert.False(Is(Path.Combine(pf, "Browser", "browser.exe")));
        Assert.False(Is(Path.Combine(sysRoot, "explorer.exe")));

        // msiexec under %SystemRoot% and a Program Files root as InstallLocation never cover other programs
        var msi = JobUninstallLauncher.UninstallerDirs(new UninstallCommand(Path.Combine(sysRoot, "System32", "msiexec.exe"), "/X"), pf);
        Assert.False(JobUninstallLauncher.IsUninstallerProcess(Path.Combine(sysRoot, "System32", "dllhost.exe"), msi));
        Assert.False(JobUninstallLauncher.IsUninstallerProcess(Path.Combine(pf, "Browser", "browser.exe"), msi));
        Assert.True(JobUninstallLauncher.IsUninstallerProcess(Path.Combine(Path.GetTempPath(), "x.exe"), msi));
    }

    [Fact]
    public void Exit_interpretation_keeps_the_children_flag()
    {
        Assert.True(UninstallExit.Interpret(0, childrenStillRunning: true).ChildrenStillRunning);
        Assert.False(UninstallExit.Interpret(0).ChildrenStillRunning);
    }
}
