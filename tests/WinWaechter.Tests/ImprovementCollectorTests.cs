using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ImprovementCollectorTests
{
    sealed class FakeChange(string kind, string target) : IChange
    {
        public string Kind => kind;
        public string Target => target;
        public string Description => $"{kind} {target}";

        public ChangeRecord Apply(SessionContext ctx) =>
            new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);

        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    static IChange Ch(string kind = "autostart", string target = "x") => new FakeChange(kind, target);

    static Finding F(string id, FindingArea area = FindingArea.Autostart, bool pre = true, IChange? change = null,
        bool protectedVendor = false, Rating rating = Rating.Verdaechtig, string[]? reasons = null) =>
        new(id, area, "Titel " + id, "ort", reasons ?? ["Grund " + id], rating, pre, change ?? Ch("autostart", id),
            IsProtectedVendor: protectedVendor);

    static PrivacyRow Row(string id, bool recommended, int changes) =>
        new(new PrivacySetting(id, "Titel " + id, "Erklärung " + id, recommended, null, [], null), PrivacyState.Off,
            Enumerable.Range(0, changes).Select(_ => (RegValueChange)null!).ToList());

    static PerfSetting Perf(string id, string group, int? recommended, int current = 0, bool available = true, string? hint = null) =>
        new(id, group, "Titel " + id, "Erklärung " + id, [], current, available, null, hint, recommended, false, null);

    static SystemAction Act() =>
        new("a", "Aktion", "Los", null, false, (_, _) => Task.FromResult(new ActionOutcome(true, "ok")));

    static SecurityItem Sec(string id, IChange? change = null, bool recommended = false, SystemAction? act = null,
        Light light = Light.Red, IReadOnlyList<SecurityItem>? children = null) =>
        new(id, "Titel " + id, light, "Befund", "Erklärung " + id, Change: change, Recommended: recommended, Act: act, Children: children);

    static Improvement Imp(string id, ImprovementArea area, string title, params IChange[] changes) =>
        new(id, area, title, "weil", changes);

    static ImprovementSource Src(string name, params Improvement[] items) =>
        new(name, _ => Task.FromResult<(IReadOnlyList<Improvement>, IReadOnlyList<ActionSuggestion>)>((items, [])));

    static IReadOnlyList<IChange> PerfChanges(PerfSetting s, int r) => [Ch("perfvalue", s.Id)];

    [Fact]
    public void Only_recommendations_with_a_change_are_collected()
    {
        var findings = new[]
        {
            F("ok"),
            F("info", pre: false, rating: Rating.Info),
            new Finding("nochange", FindingArea.Autostart, "T", "o", ["r"], Rating.Verdaechtig, true, null),
            F("prot", protectedVendor: true),
            F("shell", FindingArea.Einstellung, pre: false),
        };
        Assert.Equal(["ok"], ImprovementMap.FromFindings(findings).Select(i => i.Id).ToArray());

        var privacy = ImprovementMap.FromPrivacy([Row("rec", true, 1), Row("notrec", false, 1), Row("nochg", true, 0)]);
        Assert.Equal(["privacy:rec"], privacy.Select(i => i.Id).ToArray());

        var perf = ImprovementMap.FromPerformance(
            [Perf("p1", "anim", 1), Perf("p2", "anim", null), Perf("p3", "anim", 1, current: 1), Perf("p4", "anim", 1, available: false)],
            PerfChanges);
        Assert.Equal(["perf:p1"], perf.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Areas_are_mapped()
    {
        Assert.Equal(ImprovementArea.Aufraeumen, ImprovementMap.FromFindings([F("s", FindingArea.Speicher)]).Single().Area);
        foreach (var a in new[] { FindingArea.Autostart, FindingArea.Dienst, FindingArea.Aufgabe })
            Assert.Equal(ImprovementArea.AutostartHintergrund, ImprovementMap.FromFindings([F("x", a)]).Single().Area);
        foreach (var a in new[] { FindingArea.Prozess, FindingArea.Verbindung })
            Assert.Equal(ImprovementArea.Sicherheit, ImprovementMap.FromFindings([F("x", a)]).Single().Area);

        var priv = ImprovementMap.FromPrivacy([Row("r", true, 1)]).Single();
        Assert.Equal(ImprovementArea.DatenschutzWerbung, priv.Area);
        Assert.Equal("Erklärung r", priv.Why);
        Assert.Equal("Grund a", ImprovementMap.FromFindings([F("a", reasons: ["Grund a", "zweiter"])]).Single().Why);

        var perf = ImprovementMap.FromPerformance(
            [Perf("a", "background", 1), Perf("b", "browser-background", 1), Perf("c", "anim", 1)], PerfChanges);
        Assert.Equal([ImprovementArea.AutostartHintergrund, ImprovementArea.AutostartHintergrund, ImprovementArea.Leistung],
            perf.Select(i => i.Area).ToArray());

        Assert.Equal(ImprovementArea.Sicherheit,
            ImprovementMap.FromSecurity([Sec("smartscreen", Ch("secvalue", "s"), recommended: true)]).Items.Single().Area);

        Assert.Equal("Aufräumen", ImprovementAreas.Title(ImprovementArea.Aufraeumen));
        Assert.Equal("Autostart & Hintergrund", ImprovementAreas.Title(ImprovementArea.AutostartHintergrund));
        Assert.Equal("Datenschutz & Werbung", ImprovementAreas.Title(ImprovementArea.DatenschutzWerbung));
        Assert.Equal("Sicherheit", ImprovementAreas.Title(ImprovementArea.Sicherheit));
        Assert.Equal("Leistung", ImprovementAreas.Title(ImprovementArea.Leistung));
    }

    [Fact]
    public void Security_children_are_included_and_need_both_recommended_and_change()
    {
        var kids = new[]
        {
            Sec("k1", Ch("fwrule", "k1"), recommended: true),
            Sec("k2", Ch("fwrule", "k2")),
            Sec("k3", null, recommended: true),
        };
        var r = ImprovementMap.FromSecurity([Sec("parent", children: kids)]);
        Assert.Equal(["k1"], r.Items.Select(i => i.Changes[0].Target).ToArray());
    }

    [Fact]
    public void Actions_are_separate_and_never_improvements()
    {
        var r = ImprovementMap.FromSecurity(
            [Sec("defender", change: null, act: Act(), light: Light.Red), Sec("updates", act: Act(), light: Light.Green)]);
        Assert.Empty(r.Items);
        var a = Assert.Single(r.Actions);
        Assert.Equal("defender", a.Id);
        Assert.Equal("Titel defender", a.Title);

        // a recommended change next to an act: the change is an improvement, the act stays an action
        var both = ImprovementMap.FromSecurity([Sec("x", Ch("secvalue", "x"), recommended: true, act: Act())]);
        Assert.Single(both.Items);
        Assert.Single(both.Actions);
    }

    [Fact]
    public void Sysprotect_runs_first()
    {
        var r = ImprovementMap.FromSecurity(
        [
            Sec("sysprotect", Ch("sysprotect", "Computerschutz C:\\"), recommended: true),
            Sec("smartscreen", Ch("secvalue", "s"), recommended: true),
        ]);
        Assert.True(r.Items.Single(i => i.Changes[0].Kind == "sysprotect").RunFirst);
        Assert.False(r.Items.Single(i => i.Changes[0].Kind == "secvalue").RunFirst);
    }

    [Fact]
    public async Task Duplicates_by_kind_and_target_are_merged_and_order_is_area_then_title()
    {
        var fromHeuristic = ImprovementMap.FromFindings([F("h", change: Ch("autostart", "Foo"))]).ToArray();
        var fromOptimizer = ImprovementMap.FromFindings([F("o", change: Ch("autostart", "Foo"))]).ToArray();
        var c = new ImprovementCollector([
            Src("Schutz", fromHeuristic),
            Src("Optimieren", [.. fromOptimizer,
                Imp("z", ImprovementArea.Leistung, "Zeta", Ch("perfvalue", "z")),
                Imp("b", ImprovementArea.Aufraeumen, "Beta", Ch("file", "b")),
                Imp("a", ImprovementArea.Aufraeumen, "Alpha", Ch("file", "a"))]),
        ]);
        var scan = await c.CollectAsync(null, CancellationToken.None);
        Assert.Equal(["a", "b", "h", "z"], scan.Items.Select(i => i.Id).ToArray());
        Assert.Empty(scan.Unchecked);
    }

    [Fact]
    public async Task Failing_source_is_listed_unchecked()
    {
        var progress = new List<string>();
        var c = new ImprovementCollector([
            new("Datenschutz", _ => throw new InvalidOperationException("kaputt")),
            Src("Leistung", Imp("a", ImprovementArea.Leistung, "A", Ch("perfvalue", "a"))),
        ]);
        var scan = await c.CollectAsync(new SyncProgress(progress), CancellationToken.None);
        Assert.Equal(["Datenschutz: nicht prüfbar"], scan.Unchecked.ToArray());
        Assert.Single(scan.Items);
        Assert.Equal(["Prüfe Datenschutz …", "Prüfe Leistung …"], progress);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var c = new ImprovementCollector([new("X", ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return null!; })]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.CollectAsync(null, cts.Token));
    }

    [Fact]
    public void Bloat_apps_only_when_allowed()
    {
        var pkgs = new FakeAppPackages();
        var rules = RuleSet.Default;
        BloatAppRow Bloat(string name, bool recommended)
        {
            var p = new AppPackage(name, $"{name}_1.2.3.4_neutral__8wekyb3d8bbwe", name + "_8wekyb3d8bbwe", null, false, false, "Store", true);
            var rule = new BloatAppRule(name, "App " + name, recommended);
            return new BloatAppRow(rule, p, new AppxChange(pkgs, p, rule, rules));
        }
        var rows = new[] { Bloat("Microsoft.Clipchamp", true), Bloat("Microsoft.Copilot", false) };

        var one = Assert.Single(ImprovementMap.FromBloatApps(rows, true));
        Assert.Equal(ImprovementArea.Aufraeumen, one.Area);
        Assert.Equal("App Microsoft.Clipchamp", one.Title);
        Assert.Empty(ImprovementMap.FromBloatApps(rows, false));
    }

    [Fact]
    public void Store_only_bloat_apps_are_listed_but_not_preselected()
    {
        var pkgs = new FakeAppPackages();
        var rules = RuleSet.Default;
        BloatAppRow Bloat(string name, bool inImage)
        {
            var p = new AppPackage(name, $"{name}_1.2.3.4_neutral__8wekyb3d8bbwe", name + "_8wekyb3d8bbwe", null, false, false, "Store", inImage);
            var rule = new BloatAppRule(name, "App " + name, true);
            return new BloatAppRow(rule, p, new AppxChange(pkgs, p, rule, rules));
        }

        var items = ImprovementMap.FromBloatApps([Bloat("Microsoft.Clipchamp", true), Bloat("Microsoft.BingNews", false)], true);

        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.Title == "App Microsoft.Clipchamp").Preselected);
        Assert.False(items.Single(i => i.Title == "App Microsoft.BingNews").Preselected);
        // everything else stays preselected
        Assert.True(ImprovementMap.FromFindings([F("ok")]).Single().Preselected);
    }

    [Fact]
    public void Browser_managed_hint_is_carried_as_note()
    {
        var perf = ImprovementMap.FromPerformance(
        [
            Perf("edge-background", "browser-background", 0, current: 1, hint: PerformanceScanner.ManagedHint("Edge")),
            Perf("chrome-background", "browser-background", 0, current: 1),
            Perf("storagesense", "storage", 1, hint: "anderer Hinweis"),
        ], PerfChanges);

        Assert.Equal(PerformanceScanner.ManagedHint("Edge"), perf.Single(i => i.Id == "perf:edge-background").Note);
        Assert.Null(perf.Single(i => i.Id == "perf:chrome-background").Note);
        // only the browser rows: the Leistung page says nothing else in its confirmation either
        Assert.Null(perf.Single(i => i.Id == "perf:storagesense").Note);
    }

    [Fact]
    public async Task Partial_overlap_is_deduplicated_per_change()
    {
        var x = Ch("autostart", "X");
        var c = new ImprovementCollector([
            Src("S1", Imp("A", ImprovementArea.Leistung, "A", x)),
            Src("S2", Imp("B", ImprovementArea.Leistung, "B", Ch("autostart", "X"), Ch("autostart", "Y"), Ch("autostart", "Y"))),
        ]);
        var scan = await c.CollectAsync(null, CancellationToken.None);
        var all = scan.Items.SelectMany(i => i.Changes).Select(ch => ch.Target).ToArray();
        Assert.Equal(["X", "Y"], all.OrderBy(t => t).ToArray());
        var b = scan.Items.Single(i => i.Id == "B");
        Assert.Equal("B", b.Title);
        Assert.Equal(["Y"], b.Changes.Select(ch => ch.Target).ToArray());
    }

    [Fact]
    public async Task Same_registry_value_from_different_kinds_is_merged()
    {
        var c = new ImprovementCollector([
            Src("Datenschutz", Imp("p", ImprovementArea.DatenschutzWerbung, "P", Ch("regvalue", @"HKLM\Key\Val"))),
            Src("Leistung", Imp("q", ImprovementArea.Leistung, "Q", Ch("perfvalue", @" hklm\key\VAL "))),
            Src("Sicherheit", Imp("r", ImprovementArea.Sicherheit, "R", Ch("secvalue", @"HKLM\Key\Val"))),
        ]);
        var scan = await c.CollectAsync(null, CancellationToken.None);
        Assert.Equal(["p"], scan.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Item_without_changes_is_dropped()
    {
        var c = new ImprovementCollector([Src("S", Imp("none", ImprovementArea.Leistung, "N"))]);
        var scan = await c.CollectAsync(null, CancellationToken.None);
        Assert.Empty(scan.Items);
    }
}
