using System.Diagnostics;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.Tests;

public sealed class UninstallTests : IDisposable
{
    const string Guid = "{11111111-2222-3333-4444-555555555555}";

    readonly string _tmp = Path.Combine(Path.GetTempPath(), "ww-uninst-" + System.Guid.NewGuid().ToString("N"));
    readonly CleanerPaths _paths;

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

    UninstallPlan Plan(InstalledProgram p) => UninstallPlanner.Plan(p, _paths, File.Exists);

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
        var code = await new JobUninstallLauncher().RunAsync(new UninstallCommand(Path.Combine(sys32, "cmd.exe"),
            "/c start \"\" /b \"%SystemRoot%\\System32\\ping.exe\" -n 3 127.0.0.1 >nul & exit 7"));
        sw.Stop();
        Assert.Equal(7, code);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5), $"returned after {sw.Elapsed}");
    }
}
