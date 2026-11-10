using System.Diagnostics;
using Microsoft.Win32;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class SecurityNetTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-secnet-" + Guid.NewGuid().ToString("N"));
    readonly CleanerPaths _paths;
    readonly FakeRegistry _reg = new();

    public SecurityNetTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new CleanerPaths(Path.Combine(_root, "AppData", "Local"), Path.Combine(_root, "AppData", "Roaming"),
            Path.Combine(_root, "win"), Path.Combine(_root, "pd"), Path.Combine(_root, "pf"), Path.Combine(_root, "pf86"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    const string EdgeData = @"Microsoft\Edge\User Data", ChromeData = @"Google\Chrome\User Data";
    const string ChromePolicy = @"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist";

    string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    static string Prefs(params (string Id, int Location, string? Name)[] exts) =>
        "{\"extensions\":{\"settings\":{" + string.Join(",", exts.Select(e =>
            $"\"{e.Id}\":{{\"location\":{e.Location}" + (e.Name is null ? "" : $",\"manifest\":{{\"name\":\"{e.Name}\"}}") + "}")) + "}}}";

    BrowserExtensionScanner Scanner() => new(_paths, _reg);

    static readonly string IdD = new('d', 32), IdN = new('n', 32);

    // ---------- Windows Update ----------

    [Fact]
    public void Updates_rules()
    {
        var reboot = SecurityChecks.Updates(true, new(0, null, false));
        Assert.Equal(SecurityIds.Updates, reboot.Id);
        Assert.Equal("Windows-Updates", reboot.Title);
        Assert.Equal(Light.Yellow, reboot.Light);
        Assert.StartsWith("Neustart nötig", reboot.Finding);
        Assert.Equal(SecurityActionKind.Link, reboot.Action);
        Assert.Equal(SystemLinks.WindowsUpdate, reboot.Link);
        Assert.Equal("Windows Update öffnen", reboot.ActionLabel);

        var t = new DateTime(2026, 11, 9, 8, 30, 0, DateTimeKind.Utc);
        var pending = SecurityChecks.Updates(false, new(3, t, false));
        Assert.Equal(Light.Yellow, pending.Light);
        Assert.StartsWith("3 Updates ausstehend (Stand der letzten Windows-Suche: ", pending.Finding);
        Assert.EndsWith($"{t.ToLocalTime():dd.MM.yyyy HH:mm})", pending.Finding);

        var none = SecurityChecks.Updates(false, new(0, null, false));
        Assert.Equal(Light.Green, none.Light);
        Assert.Equal("Keine ausstehenden Updates", none.Finding);

        Assert.Equal(Light.Grey, SecurityChecks.Updates(false, new(null, null, true)).Light);
        Assert.Equal(Light.Grey, SecurityChecks.Updates(false, new(null, t, false)).Light);
        Assert.Equal(Light.Grey, SecurityChecks.Updates(false, null).Light);
    }

    // ---------- orchestrator ----------

    (SecuritySources S, FakeFirewall Fw, FakeSecurityCenter Center, FakeDefender Def, FakeVolumeEncryption Enc, FakeUpdateSearch Wu, FakeListeners L)
        Sources()
    {
        var fw = new FakeFirewall();
        var center = new FakeSecurityCenter();
        var def = new FakeDefender();
        var enc = new FakeVolumeEncryption { State = new(1, 0) };
        var wu = new FakeUpdateSearch();
        var l = new FakeListeners();
        var s = new SecuritySources(fw, center, def, _reg, enc, wu, l, new FakeProcessList(), new FakeSignatures(), Scanner());
        return (s, fw, center, def, enc, wu, l);
    }

    static readonly string[] AllIds =
    [
        SecurityIds.FirewallDomain, SecurityIds.FirewallPrivate, SecurityIds.FirewallPublic, SecurityIds.Defender,
        SecurityIds.SmartScreen, SecurityIds.Uac, SecurityIds.RemoteDesktop, SecurityIds.Encryption,
        SecurityIds.Updates, SecurityIds.Listeners, SecurityIds.Extensions,
    ];

    [Fact]
    public async Task Update_search_timeout_gives_grey()
    {
        var (s, _, _, _, _, wu, _) = Sources();
        wu.State = new(null, null, true);
        var items = await new SecurityCheck(s).RunAsync(CancellationToken.None);
        Assert.Equal(Light.Grey, items.Single(i => i.Id == SecurityIds.Updates).Light);
        Assert.Equal(SecurityCheck.UpdateSearchTimeout, Assert.Single(wu.Calls));

        wu.State = new(0, null, false);
        wu.Block = TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        items = await new SecurityCheck(s, TimeSpan.FromSeconds(1)).RunAsync(CancellationToken.None);
        sw.Stop();
        var row = items.Single(i => i.Id == SecurityIds.Updates);
        Assert.Equal(Light.Grey, row.Light);
        Assert.Contains("Zeitüberschreitung", row.Finding);
        Assert.Equal("Windows-Updates", row.Title);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.Equal(AllIds, items.Select(i => i.Id));
    }

    [Fact]
    public async Task Failing_or_hanging_source_gives_grey_row_only()
    {
        var (s, fw, center, def, enc, _, _) = Sources();
        center.Throw = true;
        enc.Throw = true;
        def.StatusError = new InvalidOperationException("WMI weg");
        fw.State[FirewallProfile.Public] = false;

        var items = await new SecurityCheck(s).RunAsync(CancellationToken.None);

        Assert.Equal(AllIds, items.Select(i => i.Id));
        foreach (var id in new[] { SecurityIds.Defender, SecurityIds.Encryption })
        {
            var row = items.Single(i => i.Id == id);
            Assert.Equal(Light.Grey, row.Light);
            Assert.Equal("Nicht prüfbar", row.Finding);
        }
        Assert.Equal("Microsoft Defender", items.Single(i => i.Id == SecurityIds.Defender).Title);
        Assert.Equal("Geräteverschlüsselung", items.Single(i => i.Id == SecurityIds.Encryption).Title);
        // no third-party products assumed: firewall rows come from the firewall itself
        Assert.Equal(Light.Red, items.Single(i => i.Id == SecurityIds.FirewallPublic).Light);
        Assert.Equal("Eingeschaltet", items.Single(i => i.Id == SecurityIds.FirewallDomain).Finding);
        Assert.Equal(Light.Green, items.Single(i => i.Id == SecurityIds.Uac).Light);
    }

    [Fact]
    public async Task Failing_firewall_gives_three_grey_rows()
    {
        var (s, fw, _, _, _, _, l) = Sources();
        fw.Throw = true;
        l.Throw = true;

        var items = await new SecurityCheck(s).RunAsync(CancellationToken.None);

        Assert.Equal(AllIds, items.Select(i => i.Id));
        var rows = items.Take(3).ToList();
        Assert.All(rows, r => Assert.Equal(Light.Grey, r.Light));
        Assert.Equal(["Firewall (Domäne)", "Firewall (Privat)", "Firewall (Öffentlich)"], rows.Select(r => r.Title));
        var lis = items.Single(i => i.Id == SecurityIds.Listeners);
        Assert.Equal(Light.Grey, lis.Light);
        Assert.Equal("Im Netz lauschende Programme", lis.Title);
        Assert.Equal(Light.Green, items.Single(i => i.Id == SecurityIds.Defender).Light);
    }

    [Fact]
    public async Task Failing_firewall_read_under_takeover_stays_green()
    {
        var (s, fw, center, _, _, _, _) = Sources();
        fw.Throw = true;
        center.Fw.Add(new SecurityProduct("Norton Firewall", true, false));

        var items = await new SecurityCheck(s).RunAsync(CancellationToken.None);

        var rows = items.Take(3).ToList();
        Assert.All(rows, r => Assert.Equal(Light.Green, r.Light));
        Assert.All(rows, r => Assert.Equal("Übernommen von Norton Firewall", r.Finding));
    }

    [Fact]
    public async Task Reboot_key_is_read_by_orchestrator()
    {
        var (s, _, _, _, _, _, _) = Sources();
        _reg.SeedKey(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var items = await new SecurityCheck(s).RunAsync(CancellationToken.None);
        Assert.StartsWith("Neustart nötig", items.Single(i => i.Id == SecurityIds.Updates).Finding);
    }

    // ---------- listening programs ----------

    [Fact]
    public void Listener_rules()
    {
        var userExe = Path.Combine(_root, @"AppData\Local\x\x.exe");
        var sig = new FakeSignatures().Signed(@"C:\P\svc.exe", "Foo GmbH").Signed(userExe, "Bar AG");
        ListeningPort[] ports =
        [
            new(10, "0.0.0.0", 445), new(4, "0.0.0.0", 139), new(20, "127.0.0.1", 5000),
            new(30, "::", 7000), new(30, "0.0.0.0", 7001), new(30, "::", 7000),
        ];
        RunningProcess[] procs =
        [
            new("svc.exe", @"C:\P\svc.exe", 10), new("System", null, 4), new("loop.exe", @"C:\P\loop.exe", 20), new("x.exe", userExe, 30),
        ];

        var item = SecurityChecks.Listeners(ports, procs, sig);

        Assert.Equal(SecurityIds.Listeners, item.Id);
        Assert.Equal("Im Netz lauschende Programme", item.Title);
        Assert.Equal(Light.Yellow, item.Light);
        Assert.Equal("3 Programme", item.Finding);
        var kids = item.Children!;
        Assert.Equal(3, kids.Count);
        Assert.DoesNotContain(kids, k => k.Title == "loop.exe");

        var svc = kids.Single(k => k.Title == "svc.exe");
        Assert.Equal(Light.Green, svc.Light);
        Assert.Equal("Port 445", svc.Finding);
        Assert.StartsWith("Signiert: Foo GmbH", svc.Explanation);
        Assert.Equal(SecurityActionKind.None, svc.Action);

        var sys = kids.Single(k => k.Title == "PID 4");
        Assert.Equal(Light.Grey, sys.Light);
        Assert.StartsWith("Pfad nicht lesbar", sys.Explanation);
        Assert.Equal(SecurityActionKind.None, sys.Action);

        var x = kids.Single(k => k.Title == "x.exe");
        Assert.Equal(Light.Yellow, x.Light);
        Assert.Equal("Port 7000, 7001", x.Finding);
        Assert.StartsWith("Läuft aus einem Benutzerordner", x.Explanation);
        Assert.Equal(SecurityActionKind.DefenderScan, x.Action);
        Assert.Equal("Von Defender prüfen lassen", x.ActionLabel);
        Assert.Equal(userExe, x.ScanPath);
        Assert.DoesNotContain(@"C:\P\loop.exe", sig.VerifyCalls);
        Assert.Equal(1, sig.VerifyCalls.Count(c => c == userExe));
    }

    [Fact]
    public void Listener_unsigned_is_yellow()
    {
        var sig = new FakeSignatures().Unsigned(@"C:\P\a.exe").Set(@"C:\P\b.exe", new(SignatureState.Invalid, null, false));
        var item = SecurityChecks.Listeners([new(7, "0.0.0.0", 80), new(8, "::", 81)],
            [new("a.exe", @"C:\P\a.exe", 7), new("b.exe", @"C:\P\b.exe", 8)], sig);
        var a = item.Children!.Single(k => k.Title == "a.exe");
        Assert.Equal(Light.Yellow, a.Light);
        Assert.StartsWith("Nicht signiert", a.Explanation);
        Assert.Equal(@"C:\P\a.exe", a.ScanPath);
        var b = item.Children!.Single(k => k.Title == "b.exe");
        Assert.Equal(Light.Yellow, b.Light);
        Assert.StartsWith("Signatur ungültig", b.Explanation);
    }

    [Fact]
    public void No_listeners_is_green()
    {
        var item = SecurityChecks.Listeners([new(5, "127.0.0.1", 80), new(5, "::1", 80)], [], new FakeSignatures());
        Assert.Equal(Light.Green, item.Light);
        Assert.Equal("Keine Programme lauschen im Netz", item.Finding);
        Assert.Empty(item.Children ?? []);
    }

    [Fact]
    public void TcpTable_listening_smoke()
    {
        IReadOnlyList<ListeningPort>? ports = null;
        Assert.Null(Record.Exception(() => ports = new TcpTableConnections().Listening()));
        Assert.All(ports!, p => Assert.InRange(p.LocalPort, 1, 65535));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsUpdate_search_smoke_read_only()
    {
        // Online=false: only cached data, no download, no install.
        WindowsUpdateState? s = null;
        Assert.Null(Record.Exception(() => s = new WuaUpdateSearch().Search(TimeSpan.FromSeconds(20))));
        Assert.True(s!.Pending is null or >= 0);
    }

    // ---------- browser extensions ----------

    [Fact]
    public void Chromium_locations_and_names()
    {
        var profile = Path.Combine(_paths.LocalAppData, EdgeData, "Default");
        Write(Path.Combine(profile, "Secure Preferences"), Prefs(("A", 1, "Alpha"), ("B", 5, "Builtin"), ("C", 7, "Corp"), (IdD, 4, null)));
        var ver = Path.Combine(profile, "Extensions", IdD, "1.0_0");
        Write(Path.Combine(ver, "manifest.json"), "{\"name\":\"__MSG_appName__\",\"default_locale\":\"de\"}");
        Write(Path.Combine(ver, "_locales", "de", "messages.json"), "{\"APPNAME\":{\"message\":\"Dings\"}}");
        Write(Path.Combine(profile, "Extensions", IdD, "0.9_0", "manifest.json"), "{\"name\":\"Alt\"}");

        var exts = Scanner().Scan();

        Assert.DoesNotContain(exts, e => e.Id == "B");
        Assert.All(exts, e => { Assert.Equal("Edge", e.Browser); Assert.Equal("Default", e.Profile); });
        Assert.Equal(ExtensionOrigin.Store, exts.Single(e => e.Id == "A").Origin);
        Assert.Equal("Alpha", exts.Single(e => e.Id == "A").Name);
        Assert.Equal(ExtensionOrigin.Policy, exts.Single(e => e.Id == "C").Origin);
        var d = exts.Single(e => e.Id == IdD);
        Assert.Equal(ExtensionOrigin.External, d.Origin);
        Assert.Equal("Dings", d.Name);
    }

    [Theory]
    [InlineData(1, ExtensionOrigin.Store)]
    [InlineData(2, ExtensionOrigin.External)]
    [InlineData(3, ExtensionOrigin.External)]
    [InlineData(4, ExtensionOrigin.External)]
    [InlineData(6, ExtensionOrigin.External)]
    [InlineData(8, ExtensionOrigin.External)]
    [InlineData(7, ExtensionOrigin.Policy)]
    [InlineData(9, ExtensionOrigin.Policy)]
    [InlineData(5, ExtensionOrigin.Builtin)]
    [InlineData(10, ExtensionOrigin.Builtin)]
    [InlineData(0, ExtensionOrigin.Unknown)]
    [InlineData(11, ExtensionOrigin.Unknown)]
    [InlineData(null, ExtensionOrigin.Unknown)]
    public void Chromium_location_mapping(int? location, ExtensionOrigin origin) =>
        Assert.Equal(origin, BrowserExtensionScanner.ChromiumOrigin(location));

    [Fact]
    public void Secure_preferences_win_over_preferences()
    {
        var profile = Path.Combine(_paths.LocalAppData, ChromeData, "Profile 2");
        Write(Path.Combine(profile, "Secure Preferences"), Prefs(("A", 1, "Alpha")));
        Write(Path.Combine(profile, "Preferences"), Prefs(("A", 4, "Other"), ("G", 1, "Gamma")));
        var exts = Scanner().Scan();
        var a = exts.Single(e => e.Id == "A");
        Assert.Equal(ExtensionOrigin.Store, a.Origin);
        Assert.Equal("Alpha", a.Name);
        Assert.Equal(("Chrome", "Profile 2"), (a.Browser, a.Profile));
        Assert.Contains(exts, e => e.Id == "G");
    }

    [Fact]
    public void Chrome_forcelist_policy_marks_or_adds()
    {
        Write(Path.Combine(_paths.LocalAppData, ChromeData, "Default", "Preferences"), Prefs(("E", 1, "Echo")));
        // a non-profile folder without Preferences is ignored
        Directory.CreateDirectory(Path.Combine(_paths.LocalAppData, ChromeData, "ShaderCache"));
        _reg.Seed(Hive.LocalMachine, ChromePolicy, "1", "E;https://x", RegistryValueKind.String);
        _reg.Seed(Hive.CurrentUser, ChromePolicy, "1", "F", RegistryValueKind.String);

        var exts = Scanner().Scan();

        var e = exts.Single(x => x.Id == "E");
        Assert.Equal(ExtensionOrigin.Policy, e.Origin);
        Assert.Equal("Echo", e.Name);
        var f = exts.Single(x => x.Id == "F");
        Assert.Equal(("Chrome", "Richtlinie", "F", ExtensionOrigin.Policy), (f.Browser, f.Profile, f.Name, f.Origin));
        Assert.Equal(2, exts.Count);
    }

    [Fact]
    public void Opera_profile_is_the_stable_folder()
    {
        Write(Path.Combine(_paths.RoamingAppData, "Opera Software", "Opera Stable", "Preferences"), Prefs(("O", 1, "Oscar")));
        var o = Assert.Single(Scanner().Scan());
        Assert.Equal(("Opera", "Standard", "O", "Oscar", ExtensionOrigin.Store), (o.Browser, o.Profile, o.Id, o.Name, o.Origin));
    }

    [Fact]
    public void Firefox_extensions_json_origins()
    {
        Write(Path.Combine(_paths.RoamingAppData, @"Mozilla\Firefox\Profiles\ab12.default-release\extensions.json"), """
            {"addons":[
              {"id":"amo@x","type":"extension","location":"app-profile","sourceURI":"https://addons.mozilla.org/firefox/downloads/file/1/a.xpi","defaultLocale":{"name":"Amo"}},
              {"id":"side@x","type":"extension","location":"app-profile","sourceURI":"file:///C:/tmp/side.xpi"},
              {"id":"built@x","type":"extension","location":"app-builtin","defaultLocale":{"name":"Built"}},
              {"id":"sys@x","type":"extension","location":"app-system-defaults"},
              {"id":"theme@x","type":"theme","location":"app-profile","sourceURI":"https://addons.mozilla.org/t.xpi"}
            ]}
            """);

        var exts = Scanner().Scan();

        Assert.Equal(2, exts.Count);
        var amo = exts.Single(e => e.Id == "amo@x");
        Assert.Equal(("Firefox", "Amo", ExtensionOrigin.Store), (amo.Browser, amo.Name, amo.Origin));
        var side = exts.Single(e => e.Id == "side@x");
        Assert.Equal(ExtensionOrigin.External, side.Origin);
        Assert.Equal("side@x", side.Name);
    }

    [Fact]
    public void Corrupt_or_locked_preferences_are_skipped_and_names_fall_back()
    {
        Write(Path.Combine(_paths.LocalAppData, ChromeData, "Default", "Preferences"), "{\"extensions\":{\"settings\":{\"A\":{\"loca");
        var chrome2 = Path.Combine(_paths.LocalAppData, ChromeData, "Profile 1");
        Write(Path.Combine(chrome2, "Preferences"), Prefs(("M", 1, "__MSG_x__"), (IdN, 1, null)));
        Write(Path.Combine(chrome2, "Extensions", IdN, "2.0_0", "manifest.json"), "{\"name\":\"__MSG_y__\",\"default_locale\":\"en\"}");
        var locked = Write(Path.Combine(_paths.LocalAppData, EdgeData, "Default", "Preferences"), Prefs(("L", 1, "Locked")));
        Write(Path.Combine(_paths.RoamingAppData, "Opera Software", "Opera Stable", "Preferences"), "[1,2");

        IReadOnlyList<BrowserExtension>? exts = null;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(Record.Exception(() => exts = Scanner().Scan()));

        Assert.DoesNotContain(exts!, e => e.Id == "L");
        Assert.Equal("M", exts!.Single(e => e.Id == "M").Name);
        Assert.Equal(IdN, exts!.Single(e => e.Id == IdN).Name);
        Assert.Equal(2, exts!.Count);
    }

    [Fact]
    public void Oversized_file_is_skipped()
    {
        var path = Path.Combine(_paths.LocalAppData, ChromeData, "Default", "Preferences");
        Write(path, Prefs(("A", 1, "Alpha")));
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) fs.SetLength(BrowserExtensionScanner.MaxFileBytes + 1);
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Extension_id_cannot_leave_the_extensions_folder()
    {
        var userData = Path.Combine(_paths.LocalAppData, ChromeData);
        var profile = Path.Combine(userData, "Default");
        // ..\..\evil resolves to <User Data>\evil; the absolute id points somewhere else entirely.
        Write(Path.Combine(userData, "evil", "1.0_0", "manifest.json"), "{\"name\":\"Planted\"}");
        var outside = Path.Combine(_root, "outside");
        Write(Path.Combine(outside, "1.0_0", "manifest.json"), "{\"name\":\"Planted\"}");
        var traversal = @"..\..\evil";
        static string J(string v) => System.Text.Json.JsonSerializer.Serialize(v);
        var json = "{\"extensions\":{\"settings\":{" +
                   $"{J(traversal)}:{{\"location\":1}}," +
                   $"{J(outside)}:{{\"location\":1}}," +
                   $"{J(IdD)}:{{\"location\":1,\"manifest\":{{\"name\":\"__MSG_n__\",\"default_locale\":{J(@"..\..\x")}}}}}" +
                   "}}}";
        Write(Path.Combine(profile, "Preferences"), json);

        var exts = Scanner().Scan();

        Assert.Equal(3, exts.Count);
        Assert.DoesNotContain(exts, e => e.Name == "Planted");
        Assert.Equal(traversal, exts.Single(e => e.Id == traversal).Name);
        Assert.Equal(outside, exts.Single(e => e.Id == outside).Name);
        Assert.Equal(IdD, exts.Single(e => e.Id == IdD).Name);
        Assert.False(BrowserExtensionScanner.IsChromiumId(traversal));
        Assert.True(BrowserExtensionScanner.IsChromiumId(IdD));
    }

    [Fact]
    public void Missing_location_is_unknown_and_names_are_cleaned()
    {
        var longName = "Bad\u0007\nName" + new string('x', 150);
        Write(Path.Combine(_paths.LocalAppData, EdgeData, "Default", "Preferences"),
            "{\"extensions\":{\"settings\":{\"q\":{\"manifest\":{\"name\":" + System.Text.Json.JsonSerializer.Serialize(longName) + "}}}}}");

        var e = Assert.Single(Scanner().Scan());

        Assert.Equal(ExtensionOrigin.Unknown, e.Origin);
        Assert.Equal(BrowserExtensionScanner.MaxNameLength, e.Name.Length);
        Assert.StartsWith("BadName", e.Name);
        Assert.DoesNotContain(e.Name, char.IsControl);
        Assert.Equal(Light.Yellow, SecurityChecks.Extensions([e]).Light);
    }

    [Fact]
    public void Names_lose_format_characters_and_never_end_in_half_a_surrogate_pair()
    {
        // U+202E would flip the rest of the name around, U+200B hides itself
        Assert.Equal("Safeexe.txt", BrowserExtensionScanner.CleanName("Safe‮exe.txt​", "id"));
        Assert.Equal("id", BrowserExtensionScanner.CleanName("‮⁦", "id"));

        var emoji = new string('x', BrowserExtensionScanner.MaxNameLength - 1) + "\U0001F600tail";
        var cut = BrowserExtensionScanner.CleanName(emoji, "id");
        Assert.Equal(BrowserExtensionScanner.MaxNameLength - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        var fits = new string('x', BrowserExtensionScanner.MaxNameLength - 2) + "\U0001F600tail";
        Assert.EndsWith("\U0001F600", BrowserExtensionScanner.CleanName(fits, "id"));
    }

    [Fact]
    public async Task Defender_read_does_not_wait_for_security_center()
    {
        var (s, _, center, def, _, _, _) = Sources();
        center.Block = TimeSpan.FromMilliseconds(800);
        def.StatusDelay = TimeSpan.FromMilliseconds(800);

        var sw = Stopwatch.StartNew();
        var items = await new SecurityCheck(s, TimeSpan.FromSeconds(5)).RunAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(Light.Green, items.Single(i => i.Id == SecurityIds.Defender).Light);
        // in parallel: about 0.8 s, not 1.6 s
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1400), sw.Elapsed.ToString());
    }

    [Fact]
    public void Extension_rules()
    {
        var item = SecurityChecks.Extensions(
        [
            new("Edge", "Default", "a", "Alpha", ExtensionOrigin.Store),
            new("Chrome", "Richtlinie", "p", "p", ExtensionOrigin.Policy),
            new("Firefox", "default-release", "x", "Xeno", ExtensionOrigin.External),
            new("Edge", "Default", "u", "Unk", ExtensionOrigin.Unknown),
        ]);
        Assert.Equal(SecurityIds.Extensions, item.Id);
        Assert.Equal("Browser-Erweiterungen", item.Title);
        Assert.Equal(Light.Yellow, item.Light);
        Assert.Equal("4 Erweiterungen", item.Finding);
        Assert.Contains("im Browser unter Erweiterungen entfernen", item.Explanation);
        Assert.Equal(SecurityActionKind.None, item.Action);
        var k = item.Children!;
        Assert.Equal("Edge (Default): Alpha", k[0].Title);
        Assert.Equal((Light.Green, "Aus dem Store"), (k[0].Light, k[0].Finding));
        Assert.Equal((Light.Yellow, "Per Richtlinie erzwungen"), (k[1].Light, k[1].Finding));
        Assert.Equal((Light.Yellow, "Von außen installiert"), (k[2].Light, k[2].Finding));
        Assert.Equal((Light.Yellow, "Herkunft unbekannt"), (k[3].Light, k[3].Finding));

        var green = SecurityChecks.Extensions([new("Edge", "Default", "a", "Alpha", ExtensionOrigin.Store)]);
        Assert.Equal(Light.Green, green.Light);
    }
}
