using System.Text;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class FirewallRuleTests : IDisposable
{
    const string WinDir = @"C:\Windows";
    const string Exe = @"C:\Users\x\AppData\Local\Foo\foo.exe";
    const string RuleName = "WinWächter: foo";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ww-fwrule-" + Guid.NewGuid().ToString("N"));
    readonly FakeFirewallRules _fw = new();

    public FirewallRuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    SessionContext Ctx => new(_dir);
    FirewallRuleChange Change(string program = Exe) => new(_fw, program, WinDir);

    [Fact]
    public void Apply_adds_block_rule_and_undo_removes_exactly_it()
    {
        _fw.Seed("WinWächter: other", @"C:\o\other.exe");
        var change = Change();
        Assert.Equal("fwrule", change.Kind);
        Assert.Equal(RuleName, change.Target);
        Assert.Equal("Eingehende Verbindungen für „foo.exe“ sperren", change.Description);

        var rec = change.Apply(Ctx);

        var rule = _fw.Get(RuleName)!;
        Assert.Equal(Exe, rule.Program);
        Assert.True(rule.Inbound);
        Assert.True(rule.Block);
        Assert.True(rule.Enabled);
        Assert.Equal(RuleName, rec.Before["name"]);
        Assert.Equal(Exe, rec.Before["program"]);
        Assert.Equal("true", rec.After["present"]);
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        change.Undo(rec, Ctx);

        Assert.Null(_fw.Get(RuleName));
        Assert.NotNull(_fw.Get("WinWächter: other"));
        Assert.Equal([RuleName], _fw.Removed);
    }

    [Fact]
    public void Existing_name_refuses_apply()
    {
        _fw.Seed(RuleName, @"C:\else\foo.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => Change().Apply(Ctx));
        Assert.Equal("Eine Firewall-Regel „WinWächter: foo“ gibt es schon.", ex.Message);
        Assert.Empty(_fw.Added);
    }

    [Fact]
    public void Undo_refuses_rule_that_points_elsewhere()
    {
        var change = Change();
        var rec = change.Apply(Ctx);
        _fw.Seed(RuleName, @"C:\else\foo.exe");

        Assert.Throws<InvalidOperationException>(() => change.Undo(rec, Ctx));

        Assert.NotNull(_fw.Get(RuleName));
        Assert.Empty(_fw.Removed);
    }

    [Fact]
    public void Undo_ignores_case_of_program_and_tolerates_missing_rule()
    {
        var change = Change();
        var rec = change.Apply(Ctx);
        _fw.Seed(RuleName, Exe.ToUpperInvariant());
        change.Undo(rec, Ctx);
        Assert.Null(_fw.Get(RuleName));
        change.Undo(rec, Ctx); // already gone: nothing to do, no throw
    }

    [Fact]
    public void Drift_is_detected()
    {
        var change = Change();
        var rec = change.Apply(Ctx);
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        _fw.Seed(RuleName, @"C:\else\foo.exe");
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));

        _fw.Remove(RuleName);
        Assert.Equal(DriftState.Gone, change.CheckDrift(rec));
    }

    [Fact]
    public void Retry_data_names_rule_and_program()
    {
        var (before, after) = Change().RetryData()!.Value;
        Assert.Equal(RuleName, before["name"]);
        Assert.Equal(Exe, before["program"]);
        Assert.Equal("true", after["present"]);
    }

    [Theory]
    [InlineData(@"foo.exe")]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData(@"c:\windows\x.exe")]
    [InlineData("C:\\P\\fo\to.exe")]
    [InlineData("")]
    public void Constructor_rejects_unsuitable_programs(string program) =>
        Assert.Throws<ArgumentException>(() => Change(program));

    [Fact]
    public void Windows_lookalike_folder_is_allowed() => Assert.Equal("WinWächter: x", Change(@"C:\Windows2\x.exe").Target);

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        ChangeRecord Rec(string name, string program) => new("1", "fwrule", name, "d",
            new() { ["name"] = name, ["program"] = program }, new() { ["present"] = "true" }, ChangeStatus.Applied, DateTimeOffset.Now);

        // good record round-trips
        var ok = FirewallRuleChange.FromRecord(Rec(RuleName, Exe), _fw, WinDir);
        Assert.Equal(RuleName, ok.Target);

        // no prefix
        Assert.Throws<InvalidOperationException>(() => FirewallRuleChange.FromRecord(Rec("Remote Desktop", Exe), _fw, WinDir));
        // prefix, but another file
        var ex = Assert.Throws<InvalidOperationException>(() => FirewallRuleChange.FromRecord(Rec("WinWächter: bar", Exe), _fw, WinDir));
        Assert.Equal("Diese Firewall-Regel stammt nicht von WinWächter – Rückgängig abgelehnt.", ex.Message);
        // program under Windows
        Assert.Throws<InvalidOperationException>(() =>
            FirewallRuleChange.FromRecord(Rec("WinWächter: svchost", @"C:\Windows\System32\svchost.exe"), _fw, WinDir));
        // missing fields
        Assert.Throws<InvalidOperationException>(() => FirewallRuleChange.FromRecord(
            new("1", "fwrule", "t", "d", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now), _fw, WinDir));
    }

    [Fact]
    public void Register_rehydrates_through_the_factory()
    {
        var f = new ChangeFactory();
        FirewallRuleChange.Register(f, _fw, WinDir);
        var rec = Change().Apply(Ctx);
        var again = f.Rehydrate(rec);
        again.Undo(rec, Ctx);
        Assert.Null(_fw.Get(RuleName));
    }

    // ---------- listener rows ----------

    static readonly RuleSet Rules = RuleSet.Parse(Encoding.UTF8.GetBytes("""{ "protected": [ { "path": "\\Dell\\" } ] }"""));

    SecurityItem Row(string path, FakeSignatures sig, bool withRules = true)
    {
        var item = SecurityChecks.Listeners([new(1, "0.0.0.0", 8080)], [new(Path.GetFileName(path), path, 1)], sig,
            withRules ? _fw : null, Rules, WinDir);
        return item.Children!.Single();
    }

    [Fact]
    public void Listener_rows_offer_block_only_when_allowed()
    {
        // unsigned exe from AppData: yellow, keeps the Defender button and gets the block as second button
        var unsigned = Row(Exe, new FakeSignatures().Unsigned(Exe));
        Assert.Equal(Light.Yellow, unsigned.Light);
        Assert.Equal(SecurityActionKind.DefenderScan, unsigned.Action);
        Assert.Equal("Eingehende Verbindungen sperren", unsigned.ChangeLabel);
        Assert.IsType<FirewallRuleChange>(unsigned.Change);
        Assert.Equal(RuleName, unsigned.Change!.Target);
        Assert.False(unsigned.Recommended);

        // no rule source: no offer
        Assert.Null(Row(Exe, new FakeSignatures().Unsigned(Exe), withRules: false).ChangeLabel);

        // Microsoft-signed from a user folder: yellow by location, but no block
        var ms = Row(Exe, new FakeSignatures().Microsoft(Exe));
        Assert.Equal(Light.Yellow, ms.Light);
        Assert.Null(ms.ChangeLabel);
        Assert.Null(ms.Change);

        // protected vendor (Dell): no block
        var dell = @"C:\Users\x\AppData\Local\Dell\d.exe";
        var d = Row(dell, new FakeSignatures().Unsigned(dell));
        Assert.Equal(Light.Yellow, d.Light);
        Assert.Null(d.ChangeLabel);

        // Windows folder: no block
        var win = @"C:\Windows\Temp\w.exe";
        var w = Row(win, new FakeSignatures().Unsigned(win));
        Assert.Equal(Light.Yellow, w.Light);
        Assert.Null(w.ChangeLabel);

        // green row: no block
        var green = @"C:\Program Files\Foo\g.exe";
        var g = Row(green, new FakeSignatures().Signed(green, "Foo GmbH"));
        Assert.Equal(Light.Green, g.Light);
        Assert.Null(g.ChangeLabel);
        Assert.Null(g.Change);
    }

    [Fact]
    public void Listener_row_with_existing_rule_says_so_and_offers_nothing()
    {
        _fw.Seed(RuleName, Exe);
        var row = Row(Exe, new FakeSignatures().Unsigned(Exe));
        Assert.Null(row.Change);
        Assert.Null(row.ChangeLabel);
        Assert.EndsWith("Eingehende Verbindungen sind von WinWächter gesperrt.", row.Explanation);
        Assert.Equal(SecurityActionKind.DefenderScan, row.Action);
    }
}
