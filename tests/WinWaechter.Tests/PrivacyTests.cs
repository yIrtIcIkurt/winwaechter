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

    [Fact]
    public void Rules_privacy_list_matches_spec()
    {
        var p = _rules.Privacy;
        Assert.Equal(12, p.Count);
        Assert.Equal(new[] { "werbe-id", "angebote-diagnose", "vorschlaege", "vorschlags-apps", "tipps", "start-empfehlungen",
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
