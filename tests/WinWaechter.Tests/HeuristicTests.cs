using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class HeuristicTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-heur-" + Guid.NewGuid().ToString("N"));
    readonly FakeSignatures _sig = new();
    readonly FakeTaskStore _tasks = new();
    readonly FakeServiceControl _services = new();
    readonly FakeRegistry _reg = new();
    readonly FakeFileOps _files = new();
    // persistence scanners: test programs need not exist on disk (orphan check)
    readonly FakeFileOps _present = new() { ReportAllPresent = true };
    readonly FakeUserContext _user;
    readonly FakeProcesses _procs = new();
    readonly FakeConnections _conns = new();

    public HeuristicTests()
    {
        Directory.CreateDirectory(_root);
        _user = new FakeUserContext(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    sealed class FakeProcesses : IProcessSource
    {
        public List<ProcessSample> Samples { get; } = [];
        public Task<IReadOnlyList<ProcessSample>> SampleAsync(TimeSpan window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProcessSample>>(Samples);
    }

    sealed class FakeConnections : IConnectionSource
    {
        public List<TcpConn> Conns { get; } = [];
        public IReadOnlyList<TcpConn> Established() => Conns;
    }

    HeuristicScanner Scanner() => new(_procs, _conns, _sig, RuleSet.Default, _user, _files,
        new AutostartScanner(_reg, _user, _sig, RuleSet.Default, _present, new FakeShortcutResolver()),
        new TaskScanner(_tasks, _sig, RuleSet.Default, _present, _user),
        new ServiceScanner(_services, _sig, RuleSet.Default, _reg, _present));

    Task<IReadOnlyList<Finding>> Scan() => Scanner().ScanAsync(null, CancellationToken.None);

    string AppData(params string[] parts) => Path.Combine([_root, "AppData", "Local", .. parts]);

    static ProcessSample Proc(int pid, string exe, string? cmd = null, bool busy = false) =>
        new(pid, Path.GetFileName(exe), exe, cmd ?? exe, busy ? 60 : 1, busy);

    [Fact]
    public async Task Xmrig_with_pool_port_is_verdaechtig()
    {
        var exe = AppData("xm", "xmrig.exe");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(10, exe, exe + " -o pool:3333 --donate-level 1", busy: true));
        _conns.Conns.Add(new TcpConn(10, "1.2.3.4", 3333, "Established"));

        var f = Assert.Single(await Scan());
        Assert.Equal(Rating.Verdaechtig, f.Rating);
        Assert.True(f.Preselected);
        Assert.Equal("heur|" + exe, f.Id);
        Assert.Equal("xmrig.exe", f.Title);
        Assert.Equal(exe, f.Location);
        Assert.Equal(FindingArea.Prozess, f.Area);
        Assert.Contains(f.Reasons, r => r.Contains("Programmname") && r.Contains("Kryptominer"));
        Assert.Contains(f.Reasons, r => r.Contains("Kommandozeile"));
        Assert.Contains(f.Reasons, r => r.Contains("3333"));
    }

    [Fact]
    public async Task Signed_busy_process_is_info_or_dropped()
    {
        var exe = @"C:\Games\Cool\game.exe";
        _sig.Signed(exe, "Cool Games Ltd");
        _procs.Samples.Add(Proc(11, exe, busy: true));

        var f = Assert.Single(await Scan());
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public async Task Unsigned_appdata_exe_is_auffaellig()
    {
        var exe = AppData("Foo", "foo.exe");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(12, exe));

        var f = Assert.Single(await Scan());
        Assert.Equal(Rating.Auffaellig, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public async Task Unknown_signature_in_appdata_adds_only_location_point()
    {
        var exe = AppData("Foo", "foo.exe");
        _procs.Samples.Add(Proc(12, exe));
        var f = Assert.Single(await Scan());
        Assert.Equal(Rating.Info, f.Rating);
    }

    [Fact]
    public async Task Protected_publisher_is_dropped_even_with_load()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dell", "Sup", "dsa.exe");
        _sig.Signed(exe, "Dell Inc.");
        _procs.Samples.Add(Proc(13, exe, busy: true));
        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Protected_name_but_unsigned_is_kept()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dell", "Sup", "dsa.exe");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(13, exe, busy: true));

        var f = Assert.Single(await Scan());
        Assert.False(f.Preselected);
        Assert.Contains(f.Reasons, r => r.Contains("gibt sich als geschützter Hersteller aus, ist aber nicht gültig signiert"));
    }

    [Fact]
    public async Task Protected_name_gets_no_change_and_no_preselection()
    {
        var exe = AppData("Dell", "DellUpdate.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");   // exists, so the Ablage move would otherwise be offered
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(15, exe, exe + " --donate-level 1", busy: true));

        var f = Assert.Single(await Scan());
        Assert.True(f.IsProtectedVendor);
        Assert.Null(f.Change);
        Assert.False(f.Preselected);
        Assert.Contains(f.Reasons, r => r.Contains("gibt sich als geschützter Hersteller aus"));
    }

    [Fact]
    public async Task Protected_name_with_unknown_signature_gets_neutral_reason()
    {
        var exe = AppData("Dell", "DellUpdate.exe");
        _procs.Samples.Add(Proc(16, exe, busy: true));   // FakeSignatures: Unknown

        var f = Assert.Single(await Scan());
        Assert.True(f.IsProtectedVendor);
        Assert.Null(f.Change);
        Assert.Contains("Hersteller geschützt, Signatur nicht prüfbar", f.Reasons);
        Assert.DoesNotContain(f.Reasons, r => r.Contains("gibt sich"));
    }

    [Fact]
    public async Task Windows_folder_and_microsoft_signed_are_dropped()
    {
        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe");
        _procs.Samples.Add(Proc(14, win, busy: true));
        var ms = AppData("ms", "ms.exe");
        _sig.Microsoft(ms);
        _procs.Samples.Add(Proc(15, ms, busy: true));
        Assert.Empty(await Scan());
    }

    [Fact]
    public async Task Reasons_merged_for_same_path()
    {
        var exe = AppData("Cua", "cua.exe");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(20, exe, busy: true));
        _procs.Samples.Add(Proc(21, exe.ToUpperInvariant(), busy: true));
        _tasks.Add(@"\Cua", hidden: true, actionExe: exe);
        _services.Add("CuaSvc", imagePath: exe);

        var f = Assert.Single(await Scan());
        Assert.Equal(f.Reasons.Distinct(StringComparer.OrdinalIgnoreCase).Count(), f.Reasons.Count);
        Assert.Contains(f.Reasons, r => r.Contains("CPU"));
        Assert.Contains(f.Reasons, r => r.Contains("versteckt"));
        Assert.Equal("task", f.Change?.Kind);
        Assert.Equal(Rating.Verdaechtig, f.Rating);
    }

    [Fact]
    public async Task Persistence_only_unsigned_appdata_service_is_listed()
    {
        var exe = AppData("Svc", "svc.exe");
        _sig.Unsigned(exe);
        _services.Add("Svc", imagePath: exe);
        var f = Assert.Single(await Scan());
        Assert.Equal(Rating.Auffaellig, f.Rating);   // place 1 + unsigned 2 + persistence 2 = 5
        Assert.Equal("service", f.Change?.Kind);
    }

    [Fact]
    public async Task Existing_file_without_other_change_offers_move_to_ablage()
    {
        var exe = AppData("Foo", "foo.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(30, exe));
        var f = Assert.Single(await Scan());
        Assert.Equal("file", f.Change?.Kind);
    }

    [Fact]
    public async Task Missing_file_without_other_change_has_no_change()
    {
        var exe = AppData("Foo", "gone.exe");
        _sig.Unsigned(exe);
        _procs.Samples.Add(Proc(30, exe));
        Assert.Null(Assert.Single(await Scan()).Change);
    }

    [Theory]
    [InlineData("t-rex.exe", true)]
    [InlineData("T-Rex_v2.exe", true)]
    [InlineData("t-rex-viewer.exe", true)]      // "-" suffix still counts
    [InlineData("t-rexplorer.exe", false)]
    [InlineData("my-gminer.exe", false)]
    public async Task Miner_name_must_match_exe_file_name(string file, bool hit)
    {
        var exe = Path.Combine(@"C:\Tools", file);
        _sig.Signed(exe, "Someone");
        _procs.Samples.Add(Proc(40, exe));
        var all = await Scan();
        Assert.Equal(hit, all.Any(f => f.Rating == Rating.Auffaellig));
    }

    [Fact]
    public async Task Connection_only_hit_is_verbindung()
    {
        var exe = @"C:\Apps\x\client.exe";
        _sig.Signed(exe, "Acme");
        _procs.Samples.Add(Proc(50, exe));
        _conns.Conns.Add(new TcpConn(50, "1.1.1.1", 4444, "Established"));
        var f = Assert.Single(await Scan());
        Assert.Equal(FindingArea.Verbindung, f.Area);
        Assert.Equal(Rating.Info, f.Rating);
    }

    [Theory]
    [InlineData(0, Rating.Info)]
    [InlineData(2, Rating.Info)]
    [InlineData(3, Rating.Auffaellig)]
    [InlineData(5, Rating.Auffaellig)]
    [InlineData(6, Rating.Verdaechtig)]
    [InlineData(20, Rating.Verdaechtig)]
    public void Scoring_thresholds(int points, Rating expected) => Assert.Equal(expected, Scoring.ToRating(points));

    [Fact]
    public void Port_is_converted_from_network_order() => Assert.Equal(3333, TcpTableConnections.PortFromNetwork(0x050D));

    [Fact]
    public void Cpu_percent_uses_all_cores() =>
        Assert.Equal(25, WmiProcessSource.Percent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 4), 3);

    [Theory]
    [InlineData(@"C:\Users\x\a.exe", @"C:\Users\x\a.exe", 5, 1, true)]
    [InlineData(@"C:\Users\x\a.exe", @"C:\Users\x\A.EXE", 5, 1, true)]
    [InlineData(@"C:\Users\x\a.exe", @"C:\Users\x\b.exe", 5, 1, false)]
    [InlineData(@"C:\Users\x\a.exe", @"C:\Users\x\a.exe", 5, 5, false)]
    [InlineData(@"C:\Windows\System32\a.exe", @"C:\Windows\System32\a.exe", 5, 1, false)]
    [InlineData(@"C:\Users\x\a.exe", null, 5, 1, false)]
    public void Kill_guard(string target, string? proc, int pid, int self, bool allowed) =>
        Assert.Equal(allowed, ProcessTools.IsKillAllowed(target, proc, pid, self, @"C:\Windows"));
}
