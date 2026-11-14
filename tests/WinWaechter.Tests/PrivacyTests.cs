using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PrivacyTests : IDisposable
{
    const string AdKey = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    const string CdmKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-privacy-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly RuleSet _rules = RuleSet.Default;

    public PrivacyTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    RegValueChange Enabled() =>
        new(_reg, new PrivacyValue(Hive.CurrentUser, AdKey, "Enabled", 0), "Werbe-ID für Apps");

    const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    [Fact]
    public void Existing_ids_keep_values_and_move_to_ads_group()
    {
        var p = _rules.Privacy;
        Assert.Equal("werbung", PrivacyGroups.Ads);
        Assert.Equal(new[] { "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled", "SubscribedContent-338388Enabled" },
            p.Single(s => s.Id == "vorschlaege").Values.Select(v => v.Name));
        Assert.Equal(new[] { "SubscribedContent-338389Enabled", "SoftLandingEnabled" }, p.Single(s => s.Id == "tipps").Values.Select(v => v.Name));
        Assert.Equal("Start_IrisRecommendations", p.Single(s => s.Id == "start-empfehlungen").Values.Single().Name);
        Assert.Equal("SilentInstalledAppsEnabled", p.Single(s => s.Id == "vorschlags-apps").Values.Single().Name);
        Assert.All(new[] { "vorschlaege", "tipps", "start-empfehlungen", "vorschlags-apps" },
            id => Assert.Equal(PrivacyGroups.Ads, p.Single(s => s.Id == id).Group));
        Assert.All(p.Where(s => s.Id is "werbe-id" or "programmstarts" or "websuche" or "recall" or "diagnosedaten"), s => Assert.Null(s.Group));
    }

    [Fact]
    public void New_ad_values_are_listed_once()
    {
        var p = _rules.Privacy;
        var expected = new (string Id, string Key, string Name)[]
        {
            ("start-kontohinweise", AdvKey, "Start_AccountNotifications"),
            ("systemvorschlaege", CdmKey, "SystemPaneSuggestionsEnabled"),
            ("sperrbildschirm-tipps", CdmKey, "RotatingLockScreenOverlayEnabled"),
            ("explorer-werbung", AdvKey, "ShowSyncProviderNotifications"),
            ("begruessung-nach-updates", @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled"),
        };
        foreach (var (id, key, name) in expected)
        {
            var s = p.Single(x => x.Id == id);
            Assert.True(s.Recommended);
            Assert.Equal(PrivacyGroups.Ads, s.Group);
            Assert.False(string.IsNullOrWhiteSpace(s.Explanation));
            var v = Assert.Single(s.Values);
            Assert.Equal((Hive.CurrentUser, key, name, 0), (v.Hive, v.Key, v.Name, v.Target));
        }
        var triples = p.SelectMany(s => s.Values).Select(v => (v.Hive, v.Key.ToUpperInvariant(), v.Name.ToUpperInvariant())).ToList();
        Assert.Equal(triples.Count, triples.Distinct().Count());
    }

    [Fact]
    public void Duplicate_privacy_values_are_rejected()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
        if (!File.Exists(src)) src = Path.Combine(AppContext.BaseDirectory, "rules.json");
        var path = Path.Combine(_root, "rules-dup.json");
        var dup = "\"privacy\": [ { \"id\": \"doppelt\", \"title\": \"Doppelt\", \"recommended\": true, \"values\": [ " +
            "{ \"hive\": \"HKCU\", \"key\": \"Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\AdvertisingInfo\", \"name\": \"enabled\", \"target\": 0 } ] },";
        File.WriteAllText(path, File.ReadAllText(src).Replace("\"privacy\": [", dup));
        Assert.Throws<InvalidDataException>(() => RuleSet.Load(path));
    }
    [Fact]
    public void Rules_privacy_list_matches_spec()
    {
        var p = _rules.Privacy;
        Assert.Equal(17, p.Count);
        Assert.Equal(new[] { "werbe-id", "angebote-diagnose", "vorschlaege", "vorschlags-apps", "tipps", "start-empfehlungen",
            "start-kontohinweise", "systemvorschlaege", "sperrbildschirm-tipps", "explorer-werbung", "begruessung-nach-updates",
            "programmstarts", "websuche", "sprachliste", "feedback", "recall", "diagnosedaten" }, p.Select(s => s.Id));
        var v = p.Single(s => s.Id == "vorschlaege");
        Assert.Equal(4, v.Values.Count);
        Assert.DoesNotContain(v.Values, x => x.Name == "SystemPaneSuggestionsEnabled");
        Assert.DoesNotContain(p, s => s.Id == "aktivitaetsverlauf");
        Assert.False(p.Single(s => s.Id == "programmstarts").Recommended);
        Assert.All(p.Where(s => s.Id != "programmstarts"), s => Assert.True(s.Recommended));
        Assert.Equal(1, p.Single(s => s.Id == "diagnosedaten").Effective!.AtMost);
        Assert.All(p.Where(s => s.Id != "diagnosedaten").SelectMany(s => s.Values), x => Assert.Equal(Hive.CurrentUser, x.Hive));
        Assert.All(p.Single(s => s.Id == "diagnosedaten").Values, x => Assert.Equal(Hive.LocalMachine, x.Hive));
        Assert.Equal("recall", p.Single(s => s.Id == "recall").Requires);
        Assert.All(p.Where(s => s.Id is "websuche" or "recall" or "diagnosedaten"),
            s => Assert.Contains("„Einige dieser Einstellungen werden von Ihrer Organisation verwaltet“", s.Explanation));
        // UI copy uses "Sie" or impersonal forms, never "du"
        var duForm = new System.Text.RegularExpressions.Regex(@"\b(du|dich|dir|dein\w*)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.All(p, s => Assert.False(duForm.IsMatch(s.Title + " " + s.Explanation), s.Id));
        Assert.True(_rules.IsLeftoverIgnored("Microsoft Office"));
        Assert.True(_rules.IsLeftoverIgnored("common files"));
        Assert.True(_rules.IsLeftoverIgnored(".NET Core"));
        Assert.False(_rules.IsLeftoverIgnored("Foo"));
    }

    [Fact]
    public void Load_rejects_unknown_hive_in_privacy()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
        if (!File.Exists(src)) src = Path.Combine(AppContext.BaseDirectory, "rules.json");
        var path = Path.Combine(_root, "rules.json");
        File.WriteAllText(path, File.ReadAllText(src).Replace("\"hive\": \"HKCU\"", "\"hive\": \"HKCR\""));
        Assert.Throws<InvalidDataException>(() => RuleSet.Load(path));
    }

    [Fact]
    public void Apply_sets_dword_and_undo_restores_old_value_and_kind()
    {
        _reg.Seed(Hive.CurrentUser, AdKey, "Enabled", 1, RegistryValueKind.DWord);
        var c = Enabled();
        var rec = c.Apply(Ctx());
        Assert.Equal(0, _reg.Get(Hive.CurrentUser, AdKey, "Enabled"));
        Assert.Equal(RegistryValueKind.DWord, _reg.GetKind(Hive.CurrentUser, AdKey, "Enabled"));
        Assert.Equal("true", rec.Before["existed"]);
        Assert.Equal("1", rec.Before["value"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        c.Undo(rec, Ctx());
        Assert.Equal(1, _reg.Get(Hive.CurrentUser, AdKey, "Enabled"));
        Assert.Equal(RegistryValueKind.DWord, _reg.GetKind(Hive.CurrentUser, AdKey, "Enabled"));
    }

    [Fact]
    public void Undo_deletes_value_that_was_missing()
    {
        var c = Enabled();
        var rec = c.Apply(Ctx());
        Assert.Equal("false", rec.Before["existed"]);
        c.Undo(rec, Ctx());
        Assert.Contains(_reg.DeleteCalls, d => d.Name == "Enabled");
        Assert.Null(_reg.Get(Hive.CurrentUser, AdKey, "Enabled"));
    }

    [Fact]
    public void Undo_restores_string_kind_exactly()
    {
        _reg.Seed(Hive.CurrentUser, AdKey, "Enabled", "1", RegistryValueKind.String);
        var c = Enabled();
        var rec = c.Apply(Ctx());
        c.Undo(rec, Ctx());
        Assert.Equal("1", _reg.Get(Hive.CurrentUser, AdKey, "Enabled"));
        Assert.Equal(RegistryValueKind.String, _reg.GetKind(Hive.CurrentUser, AdKey, "Enabled"));
    }

    [Fact]
    public void Apply_refuses_binary_kind_and_writes_nothing()
    {
        _reg.Seed(Hive.CurrentUser, AdKey, "Enabled", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        Assert.Throws<InvalidOperationException>(() => Enabled().Apply(Ctx()));
        Assert.Empty(_reg.SetCalls);
    }

    [Fact]
    public void CheckDrift_changed_when_value_differs_from_target()
    {
        var c = Enabled();
        var rec = c.Apply(Ctx());
        _reg.Seed(Hive.CurrentUser, AdKey, "Enabled", 1, RegistryValueKind.DWord);
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
    }

    [Fact]
    public void FromRecord_refuses_value_not_in_privacy_list()
    {
        var evil = new ChangeRecord("1", "regvalue", @"HKCU\Software\Evil\X", "x",
            new() { ["hive"] = "HKCU", ["key"] = @"Software\Evil", ["name"] = "X", ["existed"] = "false" },
            new() { ["value"] = "0", ["kind"] = "DWord" }, ChangeStatus.Applied, DateTimeOffset.Now);
        Assert.Throws<InvalidOperationException>(() => RegValueChange.FromRecord(evil, _reg, _rules));

        var tampered = new ChangeRecord("2", "regvalue", @"HKCU\" + AdKey + @"\Enabled", "x",
            new() { ["hive"] = "HKCU", ["key"] = AdKey, ["name"] = "Enabled", ["existed"] = "false" },
            new() { ["value"] = "5", ["kind"] = "DWord" }, ChangeStatus.Applied, DateTimeOffset.Now);
        RegValueChange.FromRecord(tampered, _reg, _rules).Apply(Ctx());
        Assert.Equal(0, _reg.Get(Hive.CurrentUser, AdKey, "Enabled"));
    }

    [Fact]
    public void Factory_rehydrates_regvalue()
    {
        var rec = Enabled().Apply(Ctx());
        var f = ChangeFactory.CreateDefault(_reg, new FakeServiceControl(), new FakeTaskStore(), new FakeFileOps(), _rules);
        Assert.Equal("regvalue", f.Rehydrate(rec).Kind);
    }

    [Fact]
    public void Scanner_states_on_partial_off()
    {
        var scanner = new PrivacyScanner(_reg, _rules);
        PrivacyRow Tipps() => scanner.Scan(_ => true).Single(r => r.Setting.Id == "tipps");

        var row = Tipps();
        Assert.Equal(PrivacyState.On, row.State);
        Assert.Equal(2, row.Changes.Count);

        _reg.Seed(Hive.CurrentUser, CdmKey, "SoftLandingEnabled", 0, RegistryValueKind.DWord);
        row = Tipps();
        Assert.Equal(PrivacyState.Partial, row.State);
        Assert.Single(row.Changes);

        _reg.Seed(Hive.CurrentUser, CdmKey, "SubscribedContent-338389Enabled", 0, RegistryValueKind.DWord);
        row = Tipps();
        Assert.Equal(PrivacyState.Off, row.State);
        Assert.Empty(row.Changes);
    }

    [Fact]
    public void Scanner_diagnostics_already_minimal_from_effective_value()
    {
        const string eff = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";
        var scanner = new PrivacyScanner(_reg, _rules);
        _reg.Seed(Hive.LocalMachine, eff, "AllowTelemetry", 1, RegistryValueKind.DWord);
        var row = scanner.Scan(_ => true).Single(r => r.Setting.Id == "diagnosedaten");
        Assert.Equal(PrivacyState.AlreadyMinimal, row.State);
        Assert.Empty(row.Changes);

        _reg.Seed(Hive.LocalMachine, eff, "AllowTelemetry", 3, RegistryValueKind.DWord);
        row = scanner.Scan(_ => true).Single(r => r.Setting.Id == "diagnosedaten");
        Assert.Equal(PrivacyState.On, row.State);
        Assert.Single(row.Changes);
    }

    sealed class Ownership(bool ours, bool throws = false) : IPolicyOwnership
    {
        public bool IsOurs(Hive h, string key, string name) => throws ? throw new IOException("Journal") : ours;
    }

    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    PrivacyRow Diagnose(IPolicyOwnership? own) =>
        new PrivacyScanner(_reg, _rules, own).Scan(_ => true).Single(r => r.Setting.Id == "diagnosedaten");

    [Fact]
    public void Foreign_policy_value_is_not_overwritten_and_the_row_says_so()
    {
        _reg.Seed(Hive.LocalMachine, PolicyKey, "AllowTelemetry", 3, RegistryValueKind.DWord);

        foreach (var own in new IPolicyOwnership?[] { new Ownership(false), null, new Ownership(true, throws: true) })
        {
            var row = Diagnose(own);
            Assert.Equal(PrivacyState.On, row.State);
            Assert.Empty(row.Changes);
            Assert.Equal(PolicyValues.ForeignText, row.Note);
        }
    }

    [Fact]
    public void Own_or_missing_policy_value_is_offered()
    {
        // missing: no policy is set, WinWächter may set it
        var missing = Diagnose(new Ownership(false));
        Assert.Single(missing.Changes);
        Assert.Null(missing.Note);

        // WinWächter set it earlier and something reset it: offered again
        _reg.Seed(Hive.LocalMachine, PolicyKey, "AllowTelemetry", 3, RegistryValueKind.DWord);
        var ours = Diagnose(new Ownership(true));
        Assert.Single(ours.Changes);
        Assert.Null(ours.Note);
    }

    [Fact]
    public void Foreign_policy_at_target_counts_as_off_and_non_policy_values_ignore_ownership()
    {
        _reg.Seed(Hive.LocalMachine, PolicyKey, "AllowTelemetry", 1, RegistryValueKind.DWord);
        var row = Diagnose(new Ownership(false));
        Assert.Equal(PrivacyState.Off, row.State);
        Assert.Null(row.Note);

        // „tipps“ lives under ContentDeliveryManager (no policy key): present values are always offered
        _reg.Seed(Hive.CurrentUser, CdmKey, "SoftLandingEnabled", 1, RegistryValueKind.DWord);
        var tipps = new PrivacyScanner(_reg, _rules, new Ownership(false)).Scan(_ => true).Single(r => r.Setting.Id == "tipps");
        Assert.Equal(2, tipps.Changes.Count);
        Assert.Null(tipps.Note);
    }

    [Fact]
    public void Scanner_hides_recall_row_without_component()
    {
        var scanner = new PrivacyScanner(_reg, _rules);
        Assert.DoesNotContain(scanner.Scan(_ => false), r => r.Setting.Id == "recall");
        Assert.Contains(scanner.Scan(r => r == "recall"), r => r.Setting.Id == "recall");
    }

    [Fact]
    public async Task RecallProbe_true_only_for_exit_zero_with_feature_name()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => Task.FromResult((0, "Feature Name : Recall\r\nState : Enabled", ""))
        };
        Assert.True(await RecallProbe.IsPresentAsync(runner, default));
        Assert.EndsWith("dism.exe", runner.Calls[0].Exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/FeatureName:Recall", runner.Calls[0].Args);

        runner.Handler = (_, _) => Task.FromResult((87, "", ""));
        Assert.False(await RecallProbe.IsPresentAsync(runner, default));
        runner.Handler = (_, _) => Task.FromResult((0, "irgendwas", ""));
        Assert.False(await RecallProbe.IsPresentAsync(runner, default));
        runner.Handler = (_, _) => throw new InvalidOperationException("boom");
        Assert.False(await RecallProbe.IsPresentAsync(runner, default));
    }
}
