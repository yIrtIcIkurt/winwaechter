using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class BuildingBlocksTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-bb-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";

    [Fact]
    public void String_value_write_drift_and_undo()
    {
        var h = Hive.LocalMachine;
        Assert.False(_reg.KeyExists(h, EdgeKey));
        var before = RegDword.Snapshot(_reg, h, EdgeKey, "SmartScreenPuaEnabled");
        Assert.Equal(DriftState.Changed, RegDword.DriftString(_reg, h, EdgeKey, "SmartScreenPuaEnabled", "Warn"));

        RegDword.WriteString(_reg, h, EdgeKey, "SmartScreenPuaEnabled", "Warn");
        Assert.True(_reg.KeyExists(h, EdgeKey));
        Assert.Equal("Warn", _reg.Get(h, EdgeKey, "SmartScreenPuaEnabled"));
        Assert.Equal(RegistryValueKind.String, _reg.GetKind(h, EdgeKey, "SmartScreenPuaEnabled"));
        Assert.Equal(DriftState.Unchanged, RegDword.DriftString(_reg, h, EdgeKey, "SmartScreenPuaEnabled", "Warn"));
        Assert.Equal(DriftState.Unchanged, RegDword.DriftString(_reg, h, EdgeKey, "SmartScreenPuaEnabled", "warn"));
        Assert.Equal(DriftState.Changed, RegDword.DriftString(_reg, h, EdgeKey, "SmartScreenPuaEnabled", "Block"));

        // missing before: undo deletes the value
        RegDword.Restore(_reg, h, EdgeKey, "SmartScreenPuaEnabled", before);
        Assert.Null(_reg.GetKind(h, EdgeKey, "SmartScreenPuaEnabled"));

        // seeded REG_SZ "Off" comes back as REG_SZ "Off"
        _reg.Seed(h, EdgeKey, "Mode", "Off", RegistryValueKind.String);
        var seeded = RegDword.Snapshot(_reg, h, EdgeKey, "Mode");
        RegDword.WriteString(_reg, h, EdgeKey, "Mode", "Warn");
        Assert.Equal("Warn", _reg.Get(h, EdgeKey, "Mode"));
        RegDword.Restore(_reg, h, EdgeKey, "Mode", seeded);
        Assert.Equal("Off", _reg.Get(h, EdgeKey, "Mode"));
        Assert.Equal(RegistryValueKind.String, _reg.GetKind(h, EdgeKey, "Mode"));

        // a DWORD with the same text is not the target
        _reg.Seed(h, EdgeKey, "Num", 1, RegistryValueKind.DWord);
        Assert.Equal(DriftState.Changed, RegDword.DriftString(_reg, h, EdgeKey, "Num", "1"));
    }

    [Fact]
    public void Policy_key_rule()
    {
        Assert.True(PolicyValues.IsPolicyKey(EdgeKey));
        Assert.True(PolicyValues.IsPolicyKey(@"Software\Policies\Microsoft\Windows\WindowsCopilot"));
        Assert.True(PolicyValues.IsPolicyKey(@"\SOFTWARE\Policies\Microsoft\Edge\"));
        Assert.False(PolicyValues.IsPolicyKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"));
        Assert.False(PolicyValues.IsPolicyKey(@"SOFTWARE\Microsoft\Edge"));
        Assert.Equal("Von einer Richtlinie festgelegt", PolicyValues.ForeignText);

        // a non-policy key is never a policy, even when a value exists there
        var own = new FakePolicyOwnership();
        _reg.Seed(Hive.LocalMachine, @"SOFTWARE\Microsoft\Edge", "X", 1, RegistryValueKind.DWord);
        Assert.Equal(PolicyState.None, PolicyValues.State(_reg, own, Hive.LocalMachine, @"SOFTWARE\Microsoft\Edge", "X"));

        _reg.Seed(Hive.LocalMachine, EdgeKey, "X", 1, RegistryValueKind.DWord);
        Assert.Equal(PolicyState.Foreign, PolicyValues.State(_reg, own, Hive.LocalMachine, EdgeKey, "X"));
        own.Own(Hive.LocalMachine, EdgeKey, "X");
        Assert.Equal(PolicyState.Ours, PolicyValues.State(_reg, own, Hive.LocalMachine, EdgeKey, "X"));
    }

    static ChangeRecord PolicyRec(string kind, string hive, string key, string name, ChangeStatus s, DateTimeOffset at) =>
        new(Guid.NewGuid().ToString("N"), kind, $@"{hive}\{key}\{name}", "Test",
            new() { ["hive"] = hive, ["key"] = key, ["name"] = name, ["existed"] = "false" },
            new() { ["value"] = "1" }, s, at);

    [Fact]
    public void Newest_record_decides_ownership()
    {
        var t0 = DateTimeOffset.Now.AddHours(-2);
        var h = Hive.LocalMachine;

        // Applied, later Undone (in a newer session) -> not ours
        var a = new SessionStore(Path.Combine(_root, "a"));
        a.Create("1").Upsert(PolicyRec("secvalue", "HKLM", EdgeKey, "X", ChangeStatus.Applied, t0));
        a.Create("2").Upsert(PolicyRec("secvalue", "HKLM", EdgeKey, "X", ChangeStatus.Undone, t0.AddMinutes(5)));
        Assert.False(new JournalPolicyOwnership(a).IsOurs(h, EdgeKey, "X"));

        // Undone, later Applied -> ours (key and name compared ignoring case)
        var b = new SessionStore(Path.Combine(_root, "b"));
        var s = b.Create("1");
        s.Upsert(PolicyRec("perfvalue", "HKLM", EdgeKey, "X", ChangeStatus.Undone, t0));
        s.Upsert(PolicyRec("regvalue", "HKLM", EdgeKey, "X", ChangeStatus.Applied, t0.AddMinutes(5)));
        var own = new JournalPolicyOwnership(b);
        Assert.True(own.IsOurs(h, EdgeKey, "X"));
        Assert.True(own.IsOurs(h, EdgeKey.ToLowerInvariant() + "\\", "x"));
        Assert.False(own.IsOurs(h, EdgeKey, "Other"));
        Assert.False(own.IsOurs(Hive.CurrentUser, EdgeKey, "X"));

        // other kinds do not count
        s.Upsert(PolicyRec("autostart", "HKLM", EdgeKey, "Y", ChangeStatus.Applied, t0));
        Assert.False(new JournalPolicyOwnership(b).IsOurs(h, EdgeKey, "Y"));

        // the instance reads the journals once
        s.Upsert(PolicyRec("regvalue", "HKLM", EdgeKey, "X", ChangeStatus.Undone, t0.AddMinutes(10)));
        Assert.True(own.IsOurs(h, EdgeKey, "X"));
        Assert.False(new JournalPolicyOwnership(b).IsOurs(h, EdgeKey, "X"));

        // a missing value has no state, even when the journal says ours
        Assert.Equal(PolicyState.None, PolicyValues.State(_reg, own, h, EdgeKey, "X"));
    }

    sealed class StepChange(string target, bool fail, List<string> applied) : IChange
    {
        public string Kind => "step";
        public string Target => target;
        public string Description => "Schritt " + target;

        public ChangeRecord Apply(SessionContext ctx)
        {
            if (fail) throw new InvalidOperationException("boom");
            applied.Add(target);
            return new ChangeRecord("x", Kind, Target, Description, new(), new() { ["v"] = "1" }, ChangeStatus.Applied, DateTimeOffset.Now);
        }

        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    static Finding F(IChange c) =>
        new(c.Target, FindingArea.Autostart, c.Target, "loc", ["r"], Rating.Entbehrlich, true, c);

    OptimizerEngine Engine(ChangeFactory? factory = null) =>
        new(Array.Empty<Func<IReadOnlyList<Finding>>>(), new SessionStore(Path.Combine(_root, "eng")),
            factory ?? new ChangeFactory(), _ => new RestorePointResult(true, "ok"));

    [Fact]
    public async Task Continue_on_error_applies_the_rest()
    {
        var applied = new List<string>();
        Finding[] Three() =>
            [F(new StepChange("a", false, applied)), F(new StepChange("b", true, applied)), F(new StepChange("c", false, applied))];

        var stop = await Engine().ApplyAsync("T", Three(), null, CancellationToken.None);
        Assert.Equal(1, stop.Applied);
        Assert.Equal(1, stop.Failed);
        Assert.Equal(["a"], applied);
        Assert.Equal([ChangeStatus.Applied, ChangeStatus.Failed], stop.Session.Records.Select(r => r.Status));

        applied.Clear();
        var go = await Engine().ApplyAsync("T", Three(), null, CancellationToken.None, continueOnError: true);
        Assert.Equal(2, go.Applied);
        Assert.Equal(1, go.Failed);
        Assert.Equal(["a", "c"], applied);
        Assert.Equal([ChangeStatus.Applied, ChangeStatus.Failed, ChangeStatus.Applied], go.Session.Records.Select(r => r.Status));
        // a kind without retry data cannot be retried
        Assert.Null(go.Session.Records[1].RetryBefore);
        Assert.False(OptimizerEngine.CanRetry(go.Session.Records[1]));
    }

    [Fact]
    public async Task Failed_record_keeps_retry_data_and_retry_rebuilds_through_whitelist()
    {
        var bc = new FakeBroadcast();
        var factory = new ChangeFactory();
        factory.Register("perfvalue", r => PerfValueChange.FromRecord(r, _reg, bc));
        var engine = Engine(factory);

        var v = PerfValues.GameMode;
        _reg.Seed(v.Hive, v.Key, v.Name, 1, RegistryValueKind.DWord);
        _reg.ThrowingKeys.Add(v.Key);
        var change = new PerfValueChange(_reg, v, 0, "Spielmodus aus", bc);
        var res = await engine.ApplyAsync("T", [F(change)], null, CancellationToken.None);
        Assert.Equal(1, res.Failed);
        var failed = res.Session.Records.Single();
        Assert.Equal(ChangeStatus.Failed, failed.Status);
        Assert.Equal("HKCU", failed.RetryBefore!["hive"]);
        Assert.Equal(v.Key, failed.RetryBefore["key"]);
        Assert.Equal(v.Name, failed.RetryBefore["name"]);
        Assert.Equal("0", failed.RetryAfter!["value"]);
        Assert.True(OptimizerEngine.CanRetry(failed));

        // the retry data survives a reload from disk
        var reloaded = new SessionStore(Path.Combine(_root, "eng")).List().Single().Records.Single();
        Assert.Equal(v.Name, reloaded.RetryBefore!["name"]);

        _reg.ThrowingKeys.Clear();
        var retry = await engine.RetryAsync(reloaded, null, CancellationToken.None);
        Assert.Equal(1, retry.Applied);
        Assert.Equal(0, retry.Failed);
        Assert.Equal("Erneut versuchen: Spielmodus aus", retry.Session.Title);
        Assert.NotEqual(res.Session.Context.SessionDir, retry.Session.Context.SessionDir);
        Assert.Equal(0, _reg.Get(v.Hive, v.Key, v.Name));
        var done = retry.Session.Records.Single();
        Assert.Equal(ChangeStatus.Applied, done.Status);
        Assert.Equal("true", done.Before["existed"]);   // real old state, taken at retry time

        // a journal pointing outside the whitelist is refused by FromRecord
        var forged = failed with { RetryBefore = new() { ["hive"] = "HKLM", ["key"] = @"SOFTWARE\Evil", ["name"] = "X" } };
        Assert.True(OptimizerEngine.CanRetry(forged));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RetryAsync(forged, null, CancellationToken.None));

        // Ablage records have no retry
        var ablage = new ChangeRecord("1", "temp", @"C:\x", "Temp", new(), new(), ChangeStatus.Failed, DateTimeOffset.Now);
        Assert.False(OptimizerEngine.CanRetry(ablage));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RetryAsync(ablage, null, CancellationToken.None));
        Assert.Equal("Für diesen Eintrag ist kein erneuter Versuch möglich.", ex.Message);
        Assert.False(OptimizerEngine.CanRetry(done));
    }

    [Fact]
    public async Task Autostart_service_and_task_have_no_retry()
    {
        // Their FromRecord has no whitelist (it must keep undo working), so a failure must not be retryable.
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        _reg.Seed(Hive.CurrentUser, approved, "Tool", new byte[12], RegistryValueKind.Binary);
        _reg.ThrowingKeys.Add(approved);
        IChange[] changes =
        [
            new StartupApprovedChange(_reg, Hive.CurrentUser, approved, "Tool"),
            new ServiceStartModeChange(new FakeServiceControl(), _reg, "MissingSvc"),
            new TaskDisableChange(new FakeTaskStore(), @"\Missing\Task"),
        ];
        foreach (var c in changes) Assert.Null(c.RetryData());

        var res = await Engine().ApplyAsync("T", changes.Select(F).ToList(), null, CancellationToken.None, continueOnError: true);
        Assert.Equal(3, res.Failed);
        Assert.Equal(["autostart", "service", "task"], res.Session.Records.Select(r => r.Kind));
        Assert.All(res.Session.Records, r =>
        {
            Assert.Equal(ChangeStatus.Failed, r.Status);
            Assert.Null(r.RetryBefore);
            Assert.Null(r.RetryAfter);
            Assert.False(OptimizerEngine.CanRetry(r));
        });
    }

    sealed class LimitedChange(string? limit) : IChange, IUndoLimit
    {
        public string Kind => "limited";
        public string Target => "t";
        public string Description => "Systemschutz ändern";
        public string? UndoLimit => limit;
        public ChangeRecord Apply(SessionContext ctx) => throw new NotSupportedException();
        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    [Fact]
    public void Confirm_line_names_undo_limit()
    {
        Assert.Equal("Nur teilweise rückgängig", UndoLimits.Partial);
        Assert.Equal("Nur über den Store wiederherstellbar", UndoLimits.StoreOnly);
        Assert.Equal("Systemschutz ändern (Nur teilweise rückgängig)", ChangeText.ConfirmLine(new LimitedChange(UndoLimits.Partial)));
        Assert.Equal("Systemschutz ändern", ChangeText.ConfirmLine(new LimitedChange(null)));
        var plain = new PerfValueChange(_reg, PerfValues.GameMode, 0, "Spielmodus aus", new FakeBroadcast());
        Assert.Equal("Spielmodus aus", ChangeText.ConfirmLine(plain));
    }
}
