using System.Text;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.ContextMenu;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

/// <summary>„Erneut versuchen“ for fwrule and shellext runs the protection check of the first offer again.</summary>
public sealed class RetryGuardTests : IDisposable
{
    const string WinDir = @"C:\Windows";
    const string Own = @"C:\Apps\WinWaechter";
    const string Exe = @"C:\Users\x\AppData\Local\Foo\foo.exe";
    const string DellExe = @"C:\Users\x\AppData\Local\Dell\d.exe";
    const string Dll = @"C:\Program Files\Zip\zipshell.dll";
    static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly RuleSet Rules = RuleSet.Parse(Encoding.UTF8.GetBytes("""{ "protected": [ { "path": "\\Dell\\" } ] }"""));

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-retryguard-" + Guid.NewGuid().ToString("N"));
    readonly FakeFirewallRules _fw = new();
    readonly FakeRegistry _reg = new();
    readonly FakeSignatures _sig = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    ShellExtScanner Scanner() => new(_reg, _sig, Rules, WinDir, Own);

    (OptimizerEngine Engine, SessionStore Store) Engine()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions"));
        var factory = new ChangeFactory();
        FirewallRuleChange.Register(factory, _fw, WinDir);
        ShellExtChange.Register(factory, _reg);
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"),
            RetryGuards.Create(Scanner(), _sig, Rules, Own));
        return (engine, store);
    }

    static ChangeRecord Failed(IChange c)
    {
        var (before, after) = c.RetryData()!.Value;
        return new ChangeRecord("f1", c.Kind, c.Target, c.Description, before, after, ChangeStatus.Failed, DateTimeOffset.Now)
        {
            RetryBefore = before, RetryAfter = after,
        };
    }

    void ZipHandler()
    {
        _reg.Seed(Hive.LocalMachine, @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\ZipMenu", "", G1.ToString("B"));
        _reg.Seed(Hive.LocalMachine, $@"SOFTWARE\Classes\CLSID\{G1:B}", "", "Zip-Erweiterung");
        _reg.Seed(Hive.LocalMachine, $@"SOFTWARE\Classes\CLSID\{G1:B}\InprocServer32", "", Dll);
    }

    [Fact]
    public async Task Firewall_retry_runs_when_program_is_still_allowed()
    {
        _sig.Unsigned(Exe);
        var (engine, _) = Engine();
        var res = await engine.RetryAsync(Failed(new FirewallRuleChange(_fw, Exe, WinDir)), null, CancellationToken.None);
        Assert.Equal(1, res.Applied);
        Assert.NotNull(_fw.Get(FirewallRuleChange.RuleName(Exe)));
    }

    [Fact]
    public async Task Firewall_retry_is_refused_for_Microsoft_protected_or_own_program_and_creates_no_session()
    {
        var own = Own + @"\WinWaechter.App.exe";
        _sig.Microsoft(Exe).Unsigned(DellExe).Unsigned(own);
        var (engine, store) = Engine();

        foreach (var program in new[] { Exe, DellExe, own })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.RetryAsync(Failed(new FirewallRuleChange(_fw, program, WinDir)), null, CancellationToken.None));
            Assert.Equal(RetryGuards.FirewallRefused, ex.Message);
        }
        Assert.Empty(_fw.Added);
        Assert.Empty(store.List());   // refused before a restore point or session exists
    }

    [Fact]
    public async Task Shellext_retry_runs_while_the_entry_is_still_offered()
    {
        ZipHandler();
        _sig.Signed(Dll, "Zip GmbH");
        var (engine, _) = Engine();
        var res = await engine.RetryAsync(Failed(ShellExtChange.Block(_reg, G1, "Zip-Erweiterung")), null, CancellationToken.None);
        Assert.Equal(1, res.Applied);
        Assert.NotNull(_reg.GetKind(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
    }

    [Fact]
    public async Task Shellext_retry_is_refused_when_entry_became_Microsoft_or_is_gone()
    {
        ZipHandler();
        _sig.Microsoft(Dll);
        var (engine, store) = Engine();
        var block = Failed(ShellExtChange.Block(_reg, G1, "Zip-Erweiterung"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RetryAsync(block, null, CancellationToken.None));
        Assert.Equal(RetryGuards.ShellExtRefused, ex.Message);

        // a verb whose key is gone is not offered any more
        var verb = Failed(ShellExtChange.LegacyDisable(_reg, Hive.CurrentUser, @"Software\Classes\*\shell\Gone", "Weg"));
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RetryAsync(verb, null, CancellationToken.None));
        Assert.Equal(RetryGuards.ShellExtRefused, ex.Message);

        Assert.Null(_reg.GetKind(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task A_failing_guard_refuses_the_retry()
    {
        _sig.Throw = new InvalidOperationException("kaputt");
        ZipHandler();
        var store = new SessionStore(Path.Combine(_root, "s2"));
        var factory = new ChangeFactory();
        ShellExtChange.Register(factory, _reg);
        var guards = new Dictionary<string, Func<IChange, string?>> { ["shellext"] = _ => throw new IOException("weg") };
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"), guards);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RetryAsync(Failed(ShellExtChange.Block(_reg, G1, "Zip")), null, CancellationToken.None));
        Assert.Equal(RetryGuards.CheckFailed, ex.Message);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Guards_cover_exactly_the_kinds_whose_offer_has_a_protection_check()
    {
        var guards = RetryGuards.Create(Scanner(), _sig, Rules, Own);
        Assert.Equal(["fwrule", "shellext"], guards.Keys.Order().ToArray());
    }
}
