using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

// Browser extensions 0.4: placeholders ignored, details, assessment and the "In {Browser} entfernen" action.
// Temp profiles and fakes only; no browser is ever started.
public sealed class ExtensionAssessmentTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-ext-" + Guid.NewGuid().ToString("N"));
    readonly CleanerPaths _paths;
    readonly FakeRegistry _reg = new();

    public ExtensionAssessmentTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new CleanerPaths(Path.Combine(_root, "AppData", "Local"), Path.Combine(_root, "AppData", "Roaming"),
            Path.Combine(_root, "win"), Path.Combine(_root, "pd"), Path.Combine(_root, "pf"), Path.Combine(_root, "pf86"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    static readonly string IdA = new('a', 32), IdB = new('b', 32), IdC = new('c', 32), IdD = new('d', 32), IdE = new('e', 32);
    const string Placeholder = "cjneempfhkonkkbcmnfdibgobmhbagaj";

    string EdgeProfile => Path.Combine(_paths.LocalAppData, @"Microsoft\Edge\User Data", "Default");

    static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    IReadOnlyList<BrowserExtension> Scan() => new BrowserExtensionScanner(_paths, _reg).Scan();

    static BrowserExtension Ext(string browser, string id, string name, ExtensionOrigin origin) =>
        new(browser, "Default", id, name, origin) { Permissions = [], Hosts = [] };

    // ---------- scanner ----------

    [Fact]
    public void Sync_placeholders_are_ignored()
    {
        // the exact shape found in Edge: only empty permissions, disable reason 8192 and needs_sync
        const string empty = "{\"api\":[],\"explicit_host\":[],\"manifest_permissions\":[],\"scriptable_host\":[]}";
        Write(Path.Combine(EdgeProfile, "Secure Preferences"), "{\"extensions\":{\"settings\":{" +
            $"\"{Placeholder}\":{{\"active_permissions\":{empty},\"granted_permissions\":{empty},\"disable_reasons\":[8192],\"needs_sync\":true}}," +
            // a path that does not exist does not make it installed either
            $"\"{IdE}\":{{\"location\":1,\"path\":\"{IdE}\\\\1.0_0\"}}," +
            $"\"{IdA}\":{{\"location\":1,\"manifest\":{{\"name\":\"Echt\",\"version\":\"1.0\"}}}}," +
            // installed, found by its folder only
            $"\"{IdB}\":{{\"location\":1,\"path\":\"{IdB}\\\\2.0_0\"}}," +
            // installed but switched off
            $"\"{IdC}\":{{\"location\":1,\"disable_reasons\":[1],\"manifest\":{{\"name\":\"Aus\"}}}}" +
            "}}}");
        Write(Path.Combine(EdgeProfile, "Extensions", IdB, "2.0_0", "manifest.json"), "{\"name\":\"Ordner\",\"version\":\"2.0\"}");

        var exts = Scan();

        Assert.DoesNotContain(exts, e => e.Id == Placeholder);
        Assert.DoesNotContain(exts, e => e.Id == IdE);
        Assert.Equal([IdA, IdB, IdC], exts.Select(e => e.Id).Order().ToArray());
        Assert.Equal("Ordner", exts.Single(e => e.Id == IdB).Name);
        Assert.Equal("2.0", exts.Single(e => e.Id == IdB).Version);
        Assert.False(exts.Single(e => e.Id == IdA).Disabled);
        Assert.True(exts.Single(e => e.Id == IdC).Disabled);

        var item = SecurityChecks.Extensions(exts, _ => true);
        Assert.Equal(Light.Green, item.Light);
        Assert.Equal(("Alle in Ordnung", "In Ordnung"), (item.Finding, item.Chip));
        Assert.Equal(3, item.Children!.Count);
        var off = item.Children.Single(c => c.Title.EndsWith(": Aus", StringComparison.Ordinal));
        Assert.Equal((Light.Grey, "Deaktiviert"), (off.Light, off.Chip));
        Assert.Equal(SecurityActionKind.None, off.Action);
        Assert.DoesNotContain("Entfernen empfohlen", off.Explanation);
    }

    [Fact]
    public void Permissions_are_summarised_in_german()
    {
        Assert.Equal(
        [
            "Darf alle Webseiten lesen und ändern", "Darf mit Programmen auf dem PC sprechen", "Darf Ihren Internetverkehr umleiten",
            "Darf Webanfragen mitlesen oder umleiten", "Darf Cookies lesen", "Darf den Verlauf lesen", "Sieht Ihre geöffneten Tabs",
            "Darf Downloads verwalten", "Darf andere Erweiterungen verwalten", "Darf die Zwischenablage lesen",
        ], ExtensionRights.Describe(
            ["clipboardRead", "management", "downloads", "tabs", "history", "cookies", "webRequestBlocking", "webRequest", "proxy",
             "nativeMessaging", "storage"],
            ["https://a.example/*", "<all_urls>"]));

        foreach (var all in new[] { "<all_urls>", "*://*/*", "http://*/*", "https://*/*" })
            Assert.Equal(["Darf alle Webseiten lesen und ändern"], ExtensionRights.Describe([], [all]));
        Assert.Equal(["Darf auf 2 Webseiten zugreifen"],
            ExtensionRights.Describe([], ["https://a.example/*", "https://b.example/*", "https://a.example/*"]));
        Assert.Equal(["Darf auf 1 Webseite zugreifen"], ExtensionRights.Describe([], ["https://a.example/*"]));
        Assert.Equal(["Darf Webanfragen mitlesen oder umleiten"], ExtensionRights.Describe(["declarativeNetRequest"], []));
        Assert.Equal(["Keine besonderen Rechte"], ExtensionRights.Describe(["storage", "alarms"], []));


        // the scanner collects them from active_permissions, the manifest and content scripts
        Write(Path.Combine(EdgeProfile, "Secure Preferences"), "{\"extensions\":{\"settings\":{" +
            $"\"{IdA}\":{{\"location\":1," +
            "\"active_permissions\":{\"api\":[\"cookies\",{\"socket\":[\"x\"]}],\"explicit_host\":[\"https://y.example/*\"],\"scriptable_host\":[]}," +
            "\"manifest\":{\"name\":\"Rechte\",\"permissions\":[\"tabs\",\"https://*/*\"],\"host_permissions\":[\"https://h.example/*\"]," +
            "\"content_scripts\":[{\"matches\":[\"https://x.example/*\"]}]}}" +
            "}}}");
        var e = Assert.Single(Scan());
        Assert.Contains("cookies", e.Permissions!);
        Assert.Contains("tabs", e.Permissions!);
        Assert.Contains("socket", e.Permissions!);
        Assert.DoesNotContain("https://*/*", e.Permissions!);
        Assert.Equal(["https://*/*", "https://h.example/*", "https://x.example/*", "https://y.example/*"], e.Hosts!.Order().ToArray());
    }

    [Fact]
    public void Install_time_is_converted()
    {
        // Chromium time: microseconds since 1601-01-01 UTC; 13300000000000000 µs = 2022-06-18 04:26:40 UTC
        var expected = new DateTime(2022, 6, 18, 4, 26, 40, DateTimeKind.Utc);
        Assert.Equal(expected, BrowserExtensionScanner.ChromiumTime("13300000000000000"));
        foreach (var bad in new[] { null, "", "abc", "-1", "0", "99999999999999999999" })
            Assert.Null(BrowserExtensionScanner.ChromiumTime(bad));

        Write(Path.Combine(EdgeProfile, "Secure Preferences"), "{\"extensions\":{\"settings\":{" +
            $"\"{IdA}\":{{\"location\":1,\"first_install_time\":\"13300000000000000\",\"install_time\":\"13310000000000000\",\"manifest\":{{\"name\":\"Erst\",\"version\":\"1.2.3\"}}}}," +
            $"\"{IdB}\":{{\"location\":1,\"install_time\":\"13300000000000000\",\"manifest\":{{\"name\":\"Nur\"}}}}," +
            $"\"{IdC}\":{{\"location\":1,\"install_time\":\"kaputt\",\"manifest\":{{\"name\":\"Ohne\"}}}}" +
            "}}}");
        var exts = Scan();
        Assert.Equal(expected, exts.Single(e => e.Id == IdA).InstalledUtc);
        Assert.Equal(expected, exts.Single(e => e.Id == IdB).InstalledUtc);
        Assert.Null(exts.Single(e => e.Id == IdC).InstalledUtc);

        var row = SecurityChecks.Extensions([exts.Single(e => e.Id == IdA)]).Children!.Single();
        var date = expected.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        Assert.Contains($"Version 1.2.3 · Installiert am {date}", row.Explanation);
    }

    [Fact]
    public void Firefox_details_are_read()
    {
        Write(Path.Combine(_paths.RoamingAppData, @"Mozilla\Firefox\Profiles\ab12.default-release\extensions.json"), """
            {"addons":[
              {"id":"amo@x","type":"extension","location":"app-profile","version":"4.5","installDate":1655526400000,"active":true,
               "sourceURI":"https://addons.mozilla.org/a.xpi","defaultLocale":{"name":"Amo"},
               "userPermissions":{"permissions":["proxy"],"origins":["<all_urls>"]}},
              {"id":"off@x","type":"extension","location":"app-profile","active":false,"userDisabled":true,"sourceURI":"file:///C:/x.xpi"}
            ]}
            """);
        var exts = Scan();
        var amo = exts.Single(e => e.Id == "amo@x");
        Assert.Equal(("4.5", new DateTime(2022, 6, 18, 4, 26, 40, DateTimeKind.Utc), false),
            (amo.Version, amo.InstalledUtc, amo.Disabled));
        Assert.Equal(["proxy"], amo.Permissions!);
        Assert.Equal(["<all_urls>"], amo.Hosts!);
        Assert.True(exts.Single(e => e.Id == "off@x").Disabled);
    }

    // ---------- rules ----------

    [Fact]
    public void Store_extension_with_broad_rights_is_green_with_rights_listed()
    {
        var e = Ext("Chrome", IdA, "Breit", ExtensionOrigin.Store) with
        {
            Permissions = ["webRequest", "nativeMessaging"], Hosts = ["<all_urls>"], Version = "3.1",
        };
        var item = SecurityChecks.Extensions([e], _ => true);

        Assert.Equal((Light.Green, "Alle in Ordnung"), (item.Light, item.Finding));
        var c = item.Children!.Single();
        Assert.Equal("Chrome (Default): Breit", c.Title);
        Assert.Equal((Light.Green, "Aus dem Store"), (c.Light, c.Finding));
        Assert.Contains("Darf alle Webseiten lesen und ändern", c.Explanation);
        Assert.Contains("Darf mit Programmen auf dem PC sprechen", c.Explanation);
        Assert.Contains("Darf Webanfragen mitlesen oder umleiten", c.Explanation);
        Assert.Contains("Version 3.1", c.Explanation);
        Assert.DoesNotContain("Entfernen empfohlen", c.Explanation);
        Assert.Equal(SecurityActionKind.None, c.Action);
    }

    [Fact]
    public void Unknown_origin_extension_is_yellow_with_recommendation_and_remove_action()
    {
        var item = SecurityChecks.Extensions([Ext("Edge", IdA, "Fremd", ExtensionOrigin.Unknown)], b => b == "Edge");

        Assert.Equal((Light.Yellow, "1 Erweiterung ansehen", "Ansehen"), (item.Light, item.Finding, item.Chip));
        var c = item.Children!.Single();
        Assert.Equal((Light.Yellow, "Ansehen", "Herkunft unbekannt"), (c.Light, c.Chip, c.Finding));
        Assert.Contains("Entfernen empfohlen, wenn Sie diese Erweiterung nicht selbst installiert haben.", c.Explanation);
        Assert.Contains("Keine besonderen Rechte", c.Explanation);
        Assert.Equal((SecurityActionKind.ExtensionPage, "In Edge entfernen"), (c.Action, c.ActionLabel));
        Assert.Equal(new ExtensionTarget("Edge", IdA), c.Extension);

        // origins outside the store
        var two = SecurityChecks.Extensions(
        [
            Ext("Chrome", IdB, "Programm", ExtensionOrigin.External),
            Ext("Opera", IdC, "Hand", ExtensionOrigin.Unpacked),
            Ext("Edge", IdD, "Gut", ExtensionOrigin.Store),
        ], _ => true);
        Assert.Equal("2 Erweiterungen ansehen", two.Finding);
        Assert.Equal("Von einem Programm installiert", two.Children![0].Finding);
        Assert.Equal("In Chrome entfernen", two.Children[0].ActionLabel);
        Assert.Equal("Von Hand geladen (Entwicklermodus)", two.Children[1].Finding);
        Assert.Equal("In Opera entfernen", two.Children[1].ActionLabel);

        // no usable browser, an id the browser page cannot take, or Firefox: recommendation stays, no button
        Assert.Equal(SecurityActionKind.None,
            SecurityChecks.Extensions([Ext("Edge", IdA, "Fremd", ExtensionOrigin.Unknown)], _ => false).Children![0].Action);
        Assert.Equal(SecurityActionKind.None,
            SecurityChecks.Extensions([Ext("Edge", "../x", "Fremd", ExtensionOrigin.Unknown)], _ => true).Children![0].Action);
        Assert.False(BrowserExtensionScanner.IsChromiumId(IdA + "\n"));
        Assert.Equal(SecurityActionKind.None,
            SecurityChecks.Extensions([Ext("Edge", IdA + "\n", "Fremd", ExtensionOrigin.Unknown)], _ => true).Children![0].Action);
        var fx = SecurityChecks.Extensions([Ext("Firefox", "side@x", "Seite", ExtensionOrigin.External)], _ => true).Children![0];
        Assert.Equal(SecurityActionKind.None, fx.Action);
        Assert.Contains("Entfernen empfohlen", fx.Explanation);
    }

    [Fact]
    public void Policy_extension_has_no_remove_action()
    {
        var item = SecurityChecks.Extensions([Ext("Edge", IdA, "Firma", ExtensionOrigin.Policy)], _ => true);
        var c = item.Children!.Single();
        Assert.Equal((Light.Yellow, "Ansehen"), (c.Light, c.Chip));
        Assert.Equal("Von Ihrer Organisation oder einem Programm per Richtlinie installiert", c.Finding);
        Assert.Contains("Entfernen nur über die Richtlinie möglich.", c.Explanation);
        Assert.DoesNotContain("Entfernen empfohlen", c.Explanation);
        Assert.Equal(SecurityActionKind.None, c.Action);
        Assert.Null(c.Extension);

        // policy-only entry (no profile data): rights unknown, so no rights line
        var bare = SecurityChecks.Extensions([new BrowserExtension("Chrome", "Richtlinie", IdB, IdB, ExtensionOrigin.Policy)]);
        Assert.DoesNotContain("Keine besonderen Rechte", bare.Children![0].Explanation);
    }

    // ---------- opening the browser's own page ----------

    sealed class RecordingLauncher : IUserLauncher
    {
        public List<(string Exe, string Args)> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public void StartAsUser(string exe, string arguments)
        {
            if (Throw is not null) throw Throw;
            Calls.Add((exe, arguments));
        }
    }

    string Touch(string path) => Write(path, "MZ");

    [Fact]
    public void Remove_opens_browser_page_as_user_with_validated_id()
    {
        var edge = Touch(Path.Combine(_paths.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"));
        var chrome = Touch(Path.Combine(_paths.ProgramFiles, @"Google\Chrome\Application\chrome.exe"));
        var opera = Touch(Path.Combine(_paths.LocalAppData, @"Programs\Opera\opera.exe"));
        _reg.Seed(Hive.LocalMachine, $@"{AppPaths}\msedge.exe", "", edge);
        _reg.Seed(Hive.LocalMachine, $@"{AppPaths}\chrome.exe", "", "\"" + chrome + "\"");
        // Opera installs per user and registers itself in the user's hive
        _reg.Seed(Hive.CurrentUser, $@"{AppPaths}\opera.exe", "", "\"" + opera + "\"");
        var launcher = new RecordingLauncher();
        var pages = new ExtensionPageOpener(_reg, _paths, launcher);

        Assert.True(pages.CanOpen("Edge"));
        Assert.True(pages.CanOpen("Chrome"));
        Assert.True(pages.CanOpen("Opera"));
        Assert.False(pages.CanOpen("Firefox"));
        Assert.False(pages.CanOpen("Brave"));   // not installed here

        pages.Open("Edge", IdA);
        pages.Open("Chrome", IdB);
        pages.Open("Opera", IdC);
        Assert.Equal(
        [
            (edge, $"edge://extensions/?id={IdA}"), (chrome, $"chrome://extensions/?id={IdB}"), (opera, $"opera://extensions/?id={IdC}"),
        ], launcher.Calls);

        foreach (var bad in new[] { "", "../x", IdA.ToUpperInvariant(), IdA + " --load-extension=x", IdA + "\n", new string('q', 32), IdA[..31] })
            Assert.Throws<ExtensionPageException>(() => pages.Open("Edge", bad));
        Assert.Throws<ExtensionPageException>(() => pages.Open("Firefox", IdA));
        Assert.Equal(3, launcher.Calls.Count);

        // a failed user launch is reported with a reason, never retried with other rights
        launcher.Throw = new UserLaunchException(UninstallPlanner.NoUserLaunchReason, 5);
        var ex = Assert.Throws<ExtensionPageException>(() => pages.Open("Edge", IdA));
        Assert.StartsWith("Edge ließ sich nicht mit Ihren normalen Benutzerrechten starten", ex.Message);
        Assert.Equal(3, launcher.Calls.Count);
    }

    [Fact]
    public void Browser_outside_trusted_folders_gets_no_button()
    {
        var launcher = new RecordingLauncher();
        var pages = new ExtensionPageOpener(_reg, _paths, launcher);
        var key = $@"{AppPaths}\msedge.exe";
        var bat = Touch(Path.Combine(_paths.ProgramFiles, @"Edge\msedge.bat"));
        var candidates = new[]
        {
            Touch(Path.Combine(_root, @"evil\msedge.exe")),                                       // outside
            Path.Combine(_paths.ProgramFiles, "..", "evil", "msedge.exe"),                          // climbs out
            Touch(Path.Combine(_paths.LocalAppData, @"Temp\msedge.exe")),                           // user folder, not Programs
            bat,                                                                                    // no .exe
            Path.Combine(_paths.ProgramFiles, @"Missing\msedge.exe"),                               // does not exist
            @"\\server\share\msedge.exe",                                                           // network
            @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",                                // unexpanded
            "msedge.exe",                                                                           // relative
        };
        foreach (var c in candidates)
        {
            _reg.Seed(Hive.LocalMachine, key, "", c);
            Assert.False(pages.CanOpen("Edge"), c);
            Assert.Throws<ExtensionPageException>(() => pages.Open("Edge", IdA));
            var row = SecurityChecks.Extensions([Ext("Edge", IdA, "Fremd", ExtensionOrigin.Unknown)], pages.CanOpen).Children![0];
            Assert.Equal(SecurityActionKind.None, row.Action);
        }
        _reg.Seed(Hive.LocalMachine, key, "", 42, RegistryValueKind.DWord);
        Assert.False(pages.CanOpen("Edge"));
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void Only_the_expected_browser_exe_counts_and_the_user_hive_only_for_Opera()
    {
        var launcher = new RecordingLauncher();
        var pages = new ExtensionPageOpener(_reg, _paths, launcher);
        // a trusted folder but another program
        var other = Touch(Path.Combine(_paths.ProgramFiles, @"Other\other.exe"));
        _reg.Seed(Hive.LocalMachine, $@"{AppPaths}\msedge.exe", "", other);
        Assert.False(pages.CanOpen("Edge"));
        // Edge (and Chrome) only from HKLM: a user-written entry is ignored even when it points to a real msedge.exe
        var edge = Touch(Path.Combine(_paths.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"));
        _reg.Seed(Hive.CurrentUser, $@"{AppPaths}\msedge.exe", "", edge);
        _reg.Seed(Hive.CurrentUser, $@"{AppPaths}\chrome.exe", "", Touch(Path.Combine(_paths.ProgramFiles, @"Google\chrome.exe")));
        Assert.False(pages.CanOpen("Edge"));
        Assert.False(pages.CanOpen("Chrome"));
        // Opera from the user's hive, but only as opera.exe
        _reg.Seed(Hive.CurrentUser, $@"{AppPaths}\opera.exe", "", Touch(Path.Combine(_paths.LocalAppData, @"Programs\Opera\launcher.exe")));
        Assert.False(pages.CanOpen("Opera"));
        Assert.Equal(SecurityActionKind.None,
            SecurityChecks.Extensions([Ext("Opera", IdA, "Fremd", ExtensionOrigin.Unknown)], pages.CanOpen).Children![0].Action);
        Assert.Throws<ExtensionPageException>(() => pages.Open("Edge", IdA));
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void Security_check_offers_the_button_only_for_a_trusted_browser()
    {
        Write(Path.Combine(EdgeProfile, "Secure Preferences"),
            "{\"extensions\":{\"settings\":{" + $"\"{IdA}\":{{\"location\":4,\"manifest\":{{\"name\":\"Hand\"}}}}" + "}}}");
        var edge = Touch(Path.Combine(_paths.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"));
        _reg.Seed(Hive.LocalMachine, $@"{AppPaths}\msedge.exe", "", edge);
        var pages = new ExtensionPageOpener(_reg, _paths, new RecordingLauncher());
        var row = SecurityChecks.Extensions(Scan(), pages.CanOpen).Children!.Single();
        Assert.Equal((SecurityActionKind.ExtensionPage, "Von Hand geladen (Entwicklermodus)"), (row.Action, row.Finding));
        Assert.Equal(new ExtensionTarget("Edge", IdA), row.Extension);
    }
}
