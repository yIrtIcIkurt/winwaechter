using WinWaechter.Core.Actions;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;
using Xunit;

namespace WinWaechter.Tests;

public class ProcessEndingTests
{
    const string Win = @"C:\Windows";
    const string Svc = @"C:\Program Files\Vendor\helper.exe";
    const string App = @"C:\Users\x\AppData\Local\Foo\helper.exe";

    static ProcessGuardContext Ctx(Func<RunningProcess, bool>? prot = null, string[]? services = null,
        int selfPid = 4242, string? selfExe = @"C:\Apps\WinWaechter\WinWaechter.exe") =>
        new(Win, selfPid, selfExe, new HashSet<string>(services ?? [Svc], StringComparer.OrdinalIgnoreCase),
            prot ?? (_ => false));

    [Fact]
    public void Same_name_in_windows_folder_or_service_is_never_ended()
    {
        var c = Ctx();
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", @"C:\Windows\System32\helper.exe", 1), c));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", @"c:\windows\helper.exe", 4), c));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", Svc, 2), c));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", @"C:\Program Files\Vendor\..\Vendor\helper.exe", 5), c));
        Assert.True(ProcessGuard.MayEnd(new("helper.exe", App, 3), c));
        // sibling folder with the same prefix is not "under" C:\Windows
        Assert.True(ProcessGuard.MayEnd(new("helper.exe", @"C:\Windows2\helper.exe", 6), c));
    }

    [Fact]
    public void Unknown_path_fails_closed()
    {
        var c = Ctx();
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", null, 1), c));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", "", 1), c));
        Assert.False(ProcessGuard.MayEndBrowser(new("chrome.exe", null, 1), c));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", App, 1), Ctx(prot: _ => throw new InvalidOperationException())));
    }

    [Fact]
    public void WinWaechter_itself_is_never_ended()
    {
        var c = Ctx();
        Assert.False(ProcessGuard.MayEnd(new("other.exe", App, 4242), c));
        Assert.False(ProcessGuard.MayEnd(new("WinWaechter.exe", @"C:\Apps\WinWaechter\WinWaechter.exe", 99), c));
        Assert.False(ProcessGuard.MayEndBrowser(new("chrome.exe", @"C:\Apps\WinWaechter\WinWaechter.exe", 99), c));
        Assert.False(ProcessGuard.MayEndBrowser(new("chrome.exe", @"C:\Browsers\chrome.exe", 4242), c));
    }

    [Fact]
    public void Protected_vendor_is_never_ended_but_browser_close_skips_only_that_rule()
    {
        var edge = new RunningProcess("msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 10);
        var c = Ctx(prot: p => p.ExePath!.Contains(@"\Microsoft\"));

        Assert.False(ProcessGuard.MayEnd(edge, c));
        Assert.True(ProcessGuard.MayEndBrowser(edge, c));

        // not on the fixed list: no browser exception
        Assert.False(ProcessGuard.MayEndBrowser(new("notepad.exe", @"C:\Program Files\Microsoft\notepad.exe", 11), c));
        // browser in Windows folder or as service still refused
        Assert.False(ProcessGuard.MayEndBrowser(new("chrome.exe", @"C:\Windows\chrome.exe", 12), c));
        Assert.False(ProcessGuard.MayEndBrowser(new("chrome.exe", Svc, 13), Ctx(services: [Svc])));
        Assert.True(ProcessGuard.MayEndBrowser(new("CHROME.EXE", @"C:\Program Files\Google\Chrome\chrome.exe", 14), c));
    }

    [Fact]
    public void Low_disk_threshold()
    {
        const long Gb = 1024L * 1024 * 1024;
        Assert.True(DiskSpace.IsLow(new("C:\\", 90L * Gb, 1000L * Gb)));      // 9 % of 1 TB
        Assert.True(DiskSpace.IsLow(new("C:\\", 14 * Gb, 100 * Gb)));
        Assert.False(DiskSpace.IsLow(new("C:\\", 20 * Gb, 100 * Gb)));
        Assert.True(DiskSpace.IsLow(new("C:\\", 15 * Gb - 1, 100 * Gb)));
    }

    [Fact]
    public void Fake_ender_finds_by_name_without_exe_and_kills_only_given()
    {
        var a = new RunningProcess("helper.exe", App, 3);
        var b = new RunningProcess("other.exe", App, 4);
        var f = new FakeProcessEnder { Running = { a, b } };
        Assert.Equal([a], f.Find("HELPER"));
        Assert.Equal(1, f.Kill([a]));
        Assert.False(f.AnyRunning([a]));
        Assert.True(f.AnyRunning([b]));
    }

    [Fact]
    public void Service_images_are_matched_from_raw_image_paths()
    {
        var exe = @"C:\Program Files\X\svc.exe";
        var raws = new[]
        {
            @"""C:\Program Files\X\svc.exe"" -k grp",
            @"C:\Program Files\X\svc.exe /run",
            @"""C:\Program Files\X\svc.exe""",
        };
        foreach (var raw in raws)
            Assert.False(ProcessGuard.MayEnd(new("svc.exe", exe, 7), Ctx(services: [raw])), raw);

        var sysRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.False(ProcessGuard.MayEnd(new("svc.exe", sysRoot + @"\Tools\svc.exe", 8),
            new(@"D:\Other", 1, null, new HashSet<string> { @"%SystemRoot%\Tools\svc.exe -x" }, _ => false)));

        // an unrelated service entry does not block
        Assert.True(ProcessGuard.MayEnd(new("helper.exe", App, 3), Ctx(services: [@"""C:\Z\z.exe"" -k a"])));
    }

    [Fact]
    public void Relative_paths_fail_closed()
    {
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", "helper.exe", 1), Ctx()));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", @"..\helper.exe", 1), Ctx()));
        Assert.False(ProcessGuard.MayEnd(new("helper.exe", App, 1),
            new("Windows", 1, null, new HashSet<string>(), _ => false)));
    }
}
