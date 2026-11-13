using System.Text;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class BloatAppTests : IDisposable
{
    const string Pfn = "_8wekyb3d8bbwe";
    const string CopilotFull = "Microsoft.Copilot_155.0.4283.45_neutral__8wekyb3d8bbwe";
    const string CopilotFamily = "Microsoft.Copilot" + Pfn;
    const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-bloat-" + Guid.NewGuid().ToString("N"));
    readonly FakeAppPackages _pkgs = new();
    static readonly RuleSet Rules = RuleSet.Default;

    public BloatAppTests()
    {
        Directory.CreateDirectory(_root);
        _pkgs.ProgramFiles = _root;   // the manifest path WinAppPackages would accept is <temp>\WindowsApps\<full>\AppxManifest.xml
    }

    string Man(string full) => Path.Combine(_root, "WindowsApps", full, "AppxManifest.xml");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    // A package whose files really exist under <temp>\WindowsApps\<full> (with a manifest).
    AppPackage Installed(string name, bool inImage = true, string? full = null, string? family = null,
        bool framework = false, bool nonRemovable = false, string kind = "Store", string? location = null)
    {
        full ??= $"{name}_1.2.3.4_neutral__8wekyb3d8bbwe";
        location ??= Path.Combine(_root, "WindowsApps", full);
        Directory.CreateDirectory(location);
        File.WriteAllText(Path.Combine(location, "AppxManifest.xml"), "<Package/>");
        var p = new AppPackage(name, full, family ?? name + Pfn, location, framework, nonRemovable, kind, inImage);
        _pkgs.Add(p);
        return p;
    }

    // A package whose location is only a string the protection list looks at (nothing is created there).
    AppPackage Listed(string name, string location)
    {
        var p = new AppPackage(name, $"{name}_1.2.3.4_neutral__8wekyb3d8bbwe", name + Pfn, location, false, false, "Store", true);
        _pkgs.Add(p);
        return p;
    }

    AppxChange Change(AppPackage p) => new(_pkgs, p, Rules.FindBloatApp(p.Name)!, Rules);

    static RuleSet RulesFrom(string bloatJson, string extra = "") => RuleSet.Parse(Encoding.UTF8.GetBytes(
        "{\"protected\":[{\"name\":\"Dell\"},{\"publisher\":\"Microsoft\"}],\"bloatApps\":" + bloatJson + extra + "}"));

    // ---- rules ----

    [Fact]
    public void Only_listed_packages_are_offered()
    {
        Installed("king.com.CandyCrushSaga");
        Installed("Microsoft.Copilot", full: CopilotFull);
        Installed("Microsoft.BingSearch");                          // Microsoft, but not on the list
        Installed("Microsoft.GamingApp");                           // Nie-Liste
        Installed("Microsoft.BingNews", framework: true);
        Installed("Microsoft.BingWeather", kind: "System");
        Installed("Clipchamp.Clipchamp", nonRemovable: true);
        Installed("DellInc.DellSupportAssistforPCs");
        Installed("Contoso.Unlisted");

        var rows = new BloatAppScanner(_pkgs, Rules).Scan();

        Assert.Equal(["Candy Crush (King)", "Copilot"], rows.Select(r => r.Rule.Title));   // recommended first
        Assert.All(rows, r => Assert.Equal(r.Package.FamilyName, r.Change.Target));
    }

    [Fact]
    public void Scan_orders_recommended_first_then_by_title()
    {
        Installed("Microsoft.OutlookForWindows");
        Installed("Microsoft.BingWeather");
        Installed("Microsoft.BingNews");
        Installed("Microsoft.MicrosoftStickyNotes");

        var titles = new BloatAppScanner(_pkgs, Rules).Scan().Select(r => r.Rule.Title).ToList();

        Assert.Equal(["Microsoft News", "Wetter", "Kurznotizen", "Outlook (neu)"], titles);
    }

    [Theory]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxGamingOverlay")]
    [InlineData("Microsoft.MicrosoftEdge*")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("DellInc.Anything")]
    [InlineData("Lexware.Buchhaltung")]
    [InlineData("Microsoft.Xbox*")]   // the prefix would cover Nie-Liste names
    [InlineData("Microsoft.*")]
    [InlineData("*")]
    [InlineData("Con*toso")]
    [InlineData("Bad Name")]
    public void Load_refuses_nie_liste_and_malformed_packages(string package)
    {
        var json = $"[{{\"package\":\"{package}\",\"title\":\"X\",\"recommended\":true}}]";
        Assert.Throws<InvalidDataException>(() => RulesFrom(json));
    }

    [Fact]
    public void Load_refuses_entries_without_package_or_title()
    {
        Assert.Throws<InvalidDataException>(() => RulesFrom("[{\"title\":\"X\"}]"));
        Assert.Throws<InvalidDataException>(() => RulesFrom("[{\"package\":\"Contoso.App\"}]"));
    }

    [Fact]
    public void Bundled_list_is_valid_and_never_names_the_nie_liste()
    {
        Assert.True(Rules.BloatApps.Count >= 20);
        Assert.All(Rules.BloatApps, r =>
        {
            Assert.False(BloatApps.RuleTouchesNever(r.Package), r.Package);
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
        });
        foreach (var n in new[] { "Microsoft.GamingApp", "Microsoft.GamingServices", "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller" })
            Assert.Null(Rules.FindBloatApp(n));
        Assert.Equal("Candy Crush (King)", Rules.FindBloatApp("king.com.CandyCrushSodaSaga")!.Title);
        Assert.True(Rules.FindBloatApp("Microsoft.BingNews")!.Recommended);
        Assert.False(Rules.FindBloatApp("Microsoft.Copilot")!.Recommended);
        Assert.Equal(3, Rules.RulesVersion);
    }

    [Fact]
    public void Microsoft_rule_does_not_block_listed_packages_but_other_protection_does()
    {
        var wa = @"C:\Program Files\WindowsApps\Microsoft.Copilot_1.0_neutral__8wekyb3d8bbwe";
        // The general rules protect it (publisher Microsoft, WindowsApps path) ...
        Assert.Equal(RuleVerdict.Protected, Rules.Classify(new ItemFacts("Microsoft.Copilot", wa, "Microsoft", null)));
        Assert.Equal(RuleVerdict.Protected, Rules.Classify(new ItemFacts("Microsoft.Copilot", wa, null, null)));
        // ... for a package named in bloatApps those two entries do not apply.
        Assert.False(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", wa, "Microsoft", null)));
        Assert.False(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", wa.Replace("Program Files", "Program Files (x86)"), null, null)));
        // Everything else still protects.
        Assert.True(Rules.IsProtectedApp(new ItemFacts("DellInc.DellSupportAssistforPCs", wa, null, null)));
        Assert.True(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", wa, "Dell Inc", null)));
        Assert.True(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", @"C:\Windows\SystemApps\Microsoft.Copilot_1.0", null, null)));
        Assert.True(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", @"C:\Program Files\Lexware\x", null, null)));
        Assert.True(Rules.IsProtectedApp(new ItemFacts("Microsoft.Copilot", @"C:\Program Files\Microsoft Office\x", null, null)));
    }

    [Fact]
    public void No_bundled_bloat_app_is_protected_at_its_usual_place()
    {
        Assert.All(Rules.BloatApps, r =>
        {
            var name = r.Package.TrimEnd('*') + (r.Package.EndsWith('*') ? "Saga" : "");
            Assert.False(Rules.IsProtectedApp(new ItemFacts(name, $@"C:\Program Files\WindowsApps\{name}_1.0_x64__abc", null, null)), name);
        });
    }

    [Fact]
    public void Network_rules_never_add_bloat_apps_or_privacy()
    {
        var downloaded = RulesFrom("[{\"package\":\"Contoso.Evil\",\"title\":\"Evil\",\"recommended\":true}]",
            ",\"rulesVersion\":9,\"privacy\":[{\"id\":\"evil\",\"title\":\"E\",\"values\":[{\"hive\":\"HKCU\",\"key\":\"K\",\"name\":\"N\",\"target\":0}]}]");

        var merged = Rules.UnionWith(downloaded);

        Assert.Equal(Rules.BloatApps, merged.BloatApps);
        Assert.Equal(Rules.Privacy, merged.Privacy);
        Assert.Null(merged.FindBloatApp("Contoso.Evil"));
        Assert.Equal(9, merged.RulesVersion);
    }

    // ---- change ----

    [Fact]
    public void Apply_removes_and_marks_restorable()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        var change = Change(p);

        var rec = change.Apply(Ctx());

        Assert.Equal([CopilotFull], _pkgs.Removed);
        Assert.False(_pkgs.IsInstalled(CopilotFamily));
        Assert.Equal("appx", rec.Kind);
        Assert.Equal(CopilotFamily, rec.Target);
        Assert.Equal("„Copilot“ entfernen", rec.Description);
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal("Microsoft.Copilot", rec.Before["name"]);
        Assert.Equal(CopilotFull, rec.Before["fullName"]);
        Assert.Equal(CopilotFamily, rec.Before["family"]);
        Assert.Equal("Copilot", rec.Before["title"]);
        Assert.Equal("true", rec.After["restorable"]);
        Assert.Equal(Path.Combine(p.InstallLocation!, "AppxManifest.xml"), rec.After["manifest"]);
        Assert.Null(change.UndoLimit);
        Assert.Equal("„Copilot“ entfernen", ChangeText.ConfirmLine(change));
        Assert.True(OptimizerEngine.IsUndoable(rec));
    }

    [Fact]
    public void Store_only_package_is_marked_before_and_not_undoable()
    {
        var p = Installed("Microsoft.BingNews", inImage: false);
        _pkgs.AfterRemove = x => Directory.Delete(x.InstallLocation!, true);   // Windows deletes the files of a Store install
        var change = Change(p);

        Assert.Equal(UndoLimits.StoreOnly, change.UndoLimit);
        Assert.Equal($"„Microsoft News“ entfernen ({UndoLimits.StoreOnly})", ChangeText.ConfirmLine(change));

        var rec = change.Apply(Ctx());

        Assert.Equal("false", rec.After["restorable"]);
        Assert.False(OptimizerEngine.IsUndoable(rec));
        var ex = Assert.Throws<InvalidOperationException>(() => change.Undo(rec, Ctx()));
        Assert.Equal(UndoLimits.StoreOnly, ex.Message);
        Assert.Empty(_pkgs.Registered);
    }

    [Fact]
    public void Undo_reregisters_from_manifest()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        var rec = Change(p).Apply(Ctx());

        // Rebuilt from the journal only, like an Undo after a restart.
        var rebuilt = AppxChange.FromRecord(rec, _pkgs, Rules);
        Assert.Equal(DriftState.Unchanged, rebuilt.CheckDrift(rec));
        rebuilt.Undo(rec, Ctx());

        Assert.Equal([Path.Combine(p.InstallLocation!, "AppxManifest.xml")], _pkgs.Registered);
    }

    [Fact]
    public void Drift_is_changed_when_installed_again()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        var change = Change(p);
        var rec = change.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        _pkgs.Reinstall(p);

        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));
    }

    [Fact]
    public void Package_gone_before_apply_fails_cleanly()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        var change = Change(p);
        _pkgs.Remove(p.FullName);
        _pkgs.Removed.Clear();

        var ex = Assert.Throws<InvalidOperationException>(() => change.Apply(Ctx()));

        Assert.Equal("„Copilot“ ist nicht mehr installiert.", ex.Message);
        Assert.Empty(_pkgs.Removed);
    }

    [Fact]
    public void Failed_removal_reports_the_reason()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        _pkgs.RemoveError = "0x80073CFA: in Benutzung";

        var ex = Assert.Throws<InvalidOperationException>(() => Change(p).Apply(Ctx()));

        Assert.Equal("„Copilot“ ließ sich nicht entfernen: 0x80073CFA: in Benutzung", ex.Message);
    }

    [Fact]
    public void Apply_rechecks_list_and_protection_before_removing()
    {
        var rule = new BloatAppRule("x", "Titel", true);
        var cases = new[]
        {
            Installed("Microsoft.BingSearch"),                                  // not on the list
            Installed("Microsoft.GamingApp"),                                   // Nie-Liste
            Installed("Microsoft.BingWeather", kind: "System"),
            Installed("Clipchamp.Clipchamp", nonRemovable: true),
            Installed("Microsoft.BingNews", framework: true),
            Listed("Microsoft.MicrosoftStickyNotes", @"C:\Windows\SystemApps\Microsoft.MicrosoftStickyNotes_1.0"),
            Listed("Microsoft.People", @"C:\Program Files\Dell\People"),
        };

        foreach (var p in cases)
            Assert.Throws<InvalidOperationException>(() => new AppxChange(_pkgs, p, rule, Rules).Apply(Ctx()));
        Assert.Empty(_pkgs.Removed);
    }

    [Fact]
    public void Retry_data_rebuilds_through_FromRecord()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        var (before, after) = Change(p).RetryData()!.Value;
        var rec = new ChangeRecord("id", "appx", p.FamilyName, "x", before, after, ChangeStatus.Failed, DateTimeOffset.Now);

        var rebuilt = AppxChange.FromRecord(rec, _pkgs, Rules);

        Assert.Equal(p.FamilyName, rebuilt.Target);
        Assert.Equal("„Copilot“ entfernen", rebuilt.Description);
        Assert.Equal(["name", "fullName", "family", "title"], before.Keys);
        Assert.Empty(after);
    }

    [Fact]
    public void FromRecord_rejects_everything_not_on_the_list()
    {
        ChangeRecord Rec(string name, string full, string family, string? manifest = null) =>
            new("id", "appx", family, "x", new() { ["name"] = name, ["fullName"] = full, ["family"] = family, ["title"] = "T" },
                new() { ["restorable"] = "true", ["manifest"] = manifest }, ChangeStatus.Applied, DateTimeOffset.Now);

        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.GamingApp", "Microsoft.GamingApp_1.0_x64__8wekyb3d8bbwe", "Microsoft.GamingApp" + Pfn), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Contoso.Unlisted", "Contoso.Unlisted_1.0_x64__8wekyb3d8bbwe", "Contoso.Unlisted" + Pfn), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull + "'; calc", CopilotFamily), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, "Other.App" + Pfn), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", "Microsoft.BingNews_1.0_x64__8wekyb3d8bbwe", CopilotFamily), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, @"C:\Temp\AppxManifest.xml"), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, Path.Combine(_root, "WindowsApps", CopilotFull, "..", "evil", "AppxManifest.xml")), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, $@"C:\Program Files\WindowsApps\{CopilotFull}\AppxManifest.xml"), _pkgs, Rules));   // not the accepted programFiles
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, Man(CopilotFull) + "'"), _pkgs, Rules));
        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(
            Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, Man(CopilotFull) + "\u2019"), _pkgs, Rules));
        // a clean record passes
        AppxChange.FromRecord(Rec("Microsoft.Copilot", CopilotFull, CopilotFamily, Man(CopilotFull)), _pkgs, Rules);
    }

    [Fact]
    public void Registered_through_the_change_factory()
    {
        var f = new ChangeFactory();
        AppxChange.Register(f, _pkgs, Rules);
        var rec = new ChangeRecord("id", "appx", CopilotFamily, "x",
            new() { ["name"] = "Microsoft.Copilot", ["fullName"] = CopilotFull, ["family"] = CopilotFamily },
            new() { ["restorable"] = "false" }, ChangeStatus.Applied, DateTimeOffset.Now);

        Assert.IsType<AppxChange>(f.Rehydrate(rec));
    }

    const string MsPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void Retry_after_failure_uses_the_live_package_for_checks_removal_and_undo()
    {
        var live = Installed("Microsoft.Copilot", full: CopilotFull);
        var (before, after) = Change(live).RetryData()!.Value;
        var rec = new ChangeRecord("id", "appx", live.FamilyName, "x", before, after, ChangeStatus.Failed, DateTimeOffset.Now);
        // The package was updated meanwhile: the journal's full name is stale, the live one counts.
        _pkgs.Remove(live.FullName);
        _pkgs.Removed.Clear();
        var newer = Installed("Microsoft.Copilot", full: "Microsoft.Copilot_156.0.1.1_neutral__8wekyb3d8bbwe");

        var applied = AppxChange.FromRecord(rec, _pkgs, Rules).Apply(Ctx());

        Assert.Equal([newer.FullName], _pkgs.Removed);
        Assert.Equal("true", applied.After["restorable"]);
        Assert.Equal(Man(newer.FullName), applied.After["manifest"]);
        Assert.Equal(newer.FullName, applied.Before["fullName"]);
    }

    [Theory]
    [InlineData(true, "Store")]
    [InlineData(false, "System")]
    public void Retry_refuses_when_the_live_package_is_protected(bool nonRemovable, string kind)
    {
        var live = Installed("Microsoft.Copilot", full: CopilotFull, nonRemovable: nonRemovable, kind: kind);
        var (before, after) = Change(live).RetryData()!.Value;
        var rec = new ChangeRecord("id", "appx", live.FamilyName, "x", before, after, ChangeStatus.Failed, DateTimeOffset.Now);

        Assert.Throws<InvalidOperationException>(() => AppxChange.FromRecord(rec, _pkgs, Rules).Apply(Ctx()));

        Assert.Empty(_pkgs.Removed);
    }

    [Fact]
    public void Publisher_is_read_and_protects()
    {
        var ms = Installed("Microsoft.Copilot", full: CopilotFull);
        _pkgs.Remove(ms.FullName);
        _pkgs.Removed.Clear();
        _pkgs.Add(ms with { Publisher = MsPublisher });
        var dell = Installed("Microsoft.BingNews");
        _pkgs.Remove(dell.FullName);
        _pkgs.Add(dell with { Publisher = "CN=Dell Inc., O=Dell Inc." });
        _pkgs.Removed.Clear();

        // Microsoft as publisher does not block a listed package, Dell does.
        var rows = new BloatAppScanner(_pkgs, Rules).Scan();
        Assert.Equal(["Copilot"], rows.Select(r => r.Rule.Title));

        var dellRule = Rules.FindBloatApp("Microsoft.BingNews")!;
        Assert.Throws<InvalidOperationException>(() =>
            new AppxChange(_pkgs, dell with { Publisher = "CN=Dell Inc., O=Dell Inc." }, dellRule, Rules).Apply(Ctx()));
        Assert.Empty(_pkgs.Removed);
    }

    [Fact]
    public void Missing_or_odd_safety_fields_fail_closed()
    {
        string Json(string flags) => "{\"Name\":\"Microsoft.Copilot\",\"PackageFullName\":\"" + CopilotFull + "\",\"PackageFamilyName\":\"" + CopilotFamily
            + "\",\"InstallLocation\":\"C:\\\\x\"" + flags + "}";
        foreach (var flags in new[]
        {
            ",\"NonRemovable\":false,\"SignatureKind\":1",                                 // IsFramework missing
            ",\"IsFramework\":false,\"SignatureKind\":1",                                  // NonRemovable missing
            ",\"IsFramework\":false,\"NonRemovable\":false",                               // SignatureKind missing
            ",\"IsFramework\":\"nein\",\"NonRemovable\":false,\"SignatureKind\":1",        // not a boolean
            ",\"IsFramework\":false,\"NonRemovable\":null,\"SignatureKind\":1",
            ",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":9",           // unknown kind
        })
        {
            var runner = new FakeProcessRunner { Handler = (_, a) => Task.FromResult(a.Contains("Get-AppxPackage") ? (0, Json(flags), "") : (0, "", "")) };
            var apps = new WinAppPackages(runner, Sid, @"C:\Program Files");
            Assert.Empty(new BloatAppScanner(apps, Rules).Scan());
        }
        var ok = new FakeProcessRunner { Handler = (_, a) => Task.FromResult(a.Contains("Get-AppxPackage")
            ? (0, Json(",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":1"), "") : (0, "", "")) };
        Assert.Single(new BloatAppScanner(new WinAppPackages(ok, Sid, @"C:\Program Files"), Rules).Scan());
    }

    [Fact]
    public void Apply_refuses_a_live_package_with_unknown_safety_fields()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull, kind: "Unknown");
        Assert.Throws<InvalidOperationException>(() => Change(p).Apply(Ctx()));
        Assert.Empty(_pkgs.Removed);
    }

    [Fact]
    public void Package_outside_the_accepted_path_is_marked_store_only()
    {
        var p = Installed("Microsoft.Copilot", full: CopilotFull);
        _pkgs.ProgramFiles = @"D:\Elsewhere";   // Register would not accept a manifest in the files that are there

        var rec = Change(p).Apply(Ctx());

        Assert.Equal("false", rec.After["restorable"]);
        Assert.False(rec.After.ContainsKey("manifest") && rec.After["manifest"] is not null);
        Assert.False(OptimizerEngine.IsUndoable(rec));
    }

    [Fact]
    public void Undo_limit_after_FromRecord_follows_restorable()
    {
        ChangeRecord Rec(string restorable) =>
            new("id", "appx", CopilotFamily, "x",
                new() { ["name"] = "Microsoft.Copilot", ["fullName"] = CopilotFull, ["family"] = CopilotFamily },
                new() { ["restorable"] = restorable, ["manifest"] = restorable == "true" ? Man(CopilotFull) : null },
                ChangeStatus.Applied, DateTimeOffset.Now);

        Assert.Null(AppxChange.FromRecord(Rec("true"), _pkgs, Rules).UndoLimit);
        Assert.Equal(UndoLimits.StoreOnly, AppxChange.FromRecord(Rec("false"), _pkgs, Rules).UndoLimit);
    }

    [Theory]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("\u2018")]
    [InlineData("\u2019")]
    [InlineData("\u201A")]
    [InlineData("\u201B")]
    public void Every_quote_character_is_rejected_in_paths(string q)
    {
        Assert.True(WinAppPackages.HasQuote("a" + q + "b"));
        var runner = new FakeProcessRunner();
        var folder = CopilotFull;
        Assert.Throws<ArgumentException>(() => new WinAppPackages(runner, Sid, @"C:\Program Files").Register($@"C:\Program Files\WindowsApps\{folder}\AppxManifest.xml" + q));
        // a quote inside the programFiles value itself
        var odd = @"C:\Prog" + q + "ram";
        var apps = new WinAppPackages(runner, Sid, odd);
        Assert.Null(apps.ManifestPathFor(folder));
        Assert.Throws<ArgumentException>(() => apps.Register(Path.Combine(odd, "WindowsApps", folder, "AppxManifest.xml")));
        Assert.Empty(runner.Calls);
    }

    // ---- package interface ----

    [Fact]
    public void Package_names_with_metacharacters_are_rejected()
    {
        var runner = new FakeProcessRunner();
        var apps = new WinAppPackages(runner, Sid, @"C:\Program Files");
        var folder = CopilotFull;

        foreach (var bad in new[]
        {
            "Microsoft.Copilot_1.0_neutral__8wekyb3d8bbwe'; calc; '",
            "Microsoft.Copilot_1.0_neutral__8wekyb3d8bbwe;calc",
            "Microsoft.Copilot_1.0_neutral__8wekyb3d8bbwe\n",           // a trailing newline must not pass
            "Microsoft.Copilot_1.0_neutral__8wekyb3d8bbwe\r\nCalc",
            "Microsoft Copilot_1.0_neutral__8wekyb3d8bbwe",
            "Microsoft.Copilot_1.0_neutral__8wekyb3d8bbw",
            "",
        })
            Assert.Throws<ArgumentException>(() => apps.Remove(bad));

        foreach (var bad in new[] { CopilotFamily + "\n", "Microsoft.Copilot'_8wekyb3d8bbwe", "x;y" + Pfn, "Microsoft.Copilot" })
            Assert.Throws<ArgumentException>(() => apps.IsInstalled(bad));

        foreach (var bad in new[]
        {
            @"C:\Temp\AppxManifest.xml",
            $@"C:\Program Files\WindowsApps\{folder}\evil.xml",
            $@"C:\Program Files\WindowsApps\{folder}\..\{folder}\AppxManifest.xml",
            $@"C:\Program Files\Other\{folder}\AppxManifest.xml",
            $@"C:\Program Files\WindowsApps\Other_1.0_x64__8wekyb3d8bbwe\..\x\AppxManifest.xml",
            $@"C:\Program Files\WindowsApps\{folder}'\AppxManifest.xml",
            $@"C:\Program Files\WindowsApps\{folder}\AppxManifest.xml'",
            $@"C:\Program Files\WindowsApps\a'b_1.0_x64__8wekyb3d8bbwe\AppxManifest.xml",
            $@"C:\Program Files\WindowsApps\{folder}\AppxManifest.xml" + "\n",
            "",
        })
            Assert.Throws<ArgumentException>(() => apps.Register(bad));

        // a bad user SID blocks even the read
        foreach (var sid in new[] { "S-1-5-21-1-2-3", "S-1-5-21-1-2-3-4\n", "S-1-5-21-1-2-3-4'; calc", "", "S-1-5-18" })
            Assert.Throws<InvalidOperationException>(() => new WinAppPackages(runner, sid, @"C:\Program Files").List());

        Assert.Empty(runner.Calls);
        Assert.Throws<ArgumentException>(() => new AppxChange(_pkgs,
            new AppPackage("Microsoft.Copilot", "x'; calc", CopilotFamily, null, false, false, "Store", true),
            new BloatAppRule("Microsoft.Copilot", "Copilot", false), Rules));
    }

    [Fact]
    public void Powershell_commands_are_exact()
    {
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var runner = new FakeProcessRunner();
        var list = "{\"Name\":\"Microsoft.Copilot\",\"PackageFullName\":\"" + CopilotFull + "\",\"PackageFamilyName\":\"" + CopilotFamily
            + "\",\"Publisher\":\"CN=Microsoft Corporation, O=Microsoft Corporation\",\"InstallLocation\":\"C:\\\\Program Files\\\\WindowsApps\\\\" + CopilotFull + "\",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":1}";
        var sys = "{\"Name\":\"Microsoft.BingNews\",\"PackageFullName\":\"Microsoft.BingNews_4.0_neutral__8wekyb3d8bbwe\",\"PackageFamilyName\":\"Microsoft.BingNews"
            + Pfn + "\",\"InstallLocation\":null,\"IsFramework\":true,\"NonRemovable\":true,\"SignatureKind\":4}";
        runner.Handler = (_, args) => Task.FromResult(args.Contains("Get-AppxPackage")
            ? (0, "[" + list + "," + sys + "]", "")
            : args.Contains("Get-AppxProvisionedPackage") ? (0, "{\"DisplayName\":\"microsoft.copilot\"}", "") : (0, "", ""));
        var apps = new WinAppPackages(runner, Sid, @"C:\Program Files");

        var all = apps.List();

        Assert.Equal(2, all.Count);
        Assert.Equal(new AppPackage("Microsoft.Copilot", CopilotFull, CopilotFamily, @"C:\Program Files\WindowsApps\" + CopilotFull, false, false, "Developer", true, "CN=Microsoft Corporation, O=Microsoft Corporation"), all[0]);
        Assert.Equal(("System", true, true, false, null), (all[1].SignatureKind, all[1].IsFramework, all[1].NonRemovable, all[1].InImage, all[1].InstallLocation));

        var listCmd = $"Get-AppxPackage -User '{Sid}' | Select-Object Name,PackageFullName,PackageFamilyName,Publisher,InstallLocation,IsFramework,NonRemovable,SignatureKind | ConvertTo-Json -Compress";
        string Args(string cmd) => $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{cmd}\"";
        Assert.Equal(
        [
            (ps, Args(listCmd)),
            (ps, Args("Get-AppxProvisionedPackage -Online | Select-Object DisplayName | ConvertTo-Json -Compress")),
        ], runner.Calls);

        runner.Calls.Clear();
        Assert.True(apps.IsInstalled(CopilotFamily));
        Assert.False(apps.IsInstalled("Other.App" + Pfn));
        apps.Remove(CopilotFull);
        var manifest = $@"C:\Program Files\WindowsApps\{CopilotFull}\AppxManifest.xml";
        apps.Register(manifest);

        Assert.Equal(
        [
            (ps, Args(listCmd)),
            (ps, Args(listCmd)),
            (ps, Args($"Remove-AppxPackage -Package '{CopilotFull}'")),
            (ps, Args($"Add-AppxPackage -Register '{manifest}' -DisableDevelopmentMode")),
        ], runner.Calls);
    }

    [Fact]
    public void Single_package_object_and_empty_output_are_read()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => Task.FromResult((0, "", "")) };
        Assert.Empty(new WinAppPackages(runner, Sid, @"C:\Program Files").List());

        runner.Handler = (_, a) => Task.FromResult(a.Contains("Get-AppxPackage")
            ? (0, "{\"Name\":\"A.B\",\"PackageFullName\":\"A.B_1.0_neutral__8wekyb3d8bbwe\",\"PackageFamilyName\":\"A.B" + Pfn + "\",\"InstallLocation\":\"C:\\\\x\",\"IsFramework\":false,\"NonRemovable\":false,\"SignatureKind\":3}", "")
            : (1, "", "Der angeforderte Vorgang erfordert erhöhte Rechte."));
        var one = new WinAppPackages(runner, Sid, @"C:\Program Files").List();

        Assert.Equal("Store", Assert.Single(one).SignatureKind);
        Assert.False(one[0].InImage);   // the image list was not readable: the conservative label stays
    }

    [Fact]
    public void Nonzero_exit_throws_with_the_error_text()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => Task.FromResult((1, "", " Zugriff verweigert \r\n")) };
        var apps = new WinAppPackages(runner, Sid, @"C:\Program Files");

        Assert.Equal("Zugriff verweigert", Assert.Throws<InvalidOperationException>(() => apps.Remove(CopilotFull)).Message);
        Assert.Throws<InvalidOperationException>(() => apps.Register($@"C:\Program Files\WindowsApps\{CopilotFull}\AppxManifest.xml"));
        Assert.Throws<InvalidOperationException>(() => apps.List());
        Assert.Throws<InvalidOperationException>(() => apps.IsInstalled(CopilotFamily));
    }

    [Fact]
    public void Store_link_is_validated()
    {
        Assert.Equal("ms-windows-store://pdp/?PFN=" + CopilotFamily, SystemLinks.StoreLink(CopilotFamily));
        Assert.True(SystemLinks.IsStoreLink(SystemLinks.StoreLink(CopilotFamily)));

        Assert.Throws<ArgumentException>(() => SystemLinks.StoreLink("x&y" + Pfn));
        Assert.Throws<ArgumentException>(() => SystemLinks.StoreLink(CopilotFamily + "\n"));
        Assert.Throws<ArgumentException>(() => SystemLinks.StoreLink(""));
        Assert.False(SystemLinks.IsStoreLink(null));
        Assert.False(SystemLinks.IsStoreLink("ms-windows-store://pdp/?PFN="));
        Assert.False(SystemLinks.IsStoreLink("ms-windows-store://pdp/?PFN=" + CopilotFamily + "\n"));
        Assert.False(SystemLinks.IsStoreLink("ms-windows-store://pdp/?PFN=" + CopilotFamily + "&x=1"));
        Assert.False(SystemLinks.IsStoreLink("https://example.com/?PFN=" + CopilotFamily));
        Assert.False(SystemLinks.IsStoreLink("MS-WINDOWS-STORE://pdp/?PFN=" + CopilotFamily));
        Assert.False(SystemLinks.IsAllowed(SystemLinks.StoreLink(CopilotFamily)));   // the general list stays untouched
    }
}
