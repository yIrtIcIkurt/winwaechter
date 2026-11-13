using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class PowerPlanTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-powerplan-" + Guid.NewGuid().ToString("N"));
    readonly FakePowerPlans _plans = new();

    public PowerPlanTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    [Fact]
    public void Apply_undo_and_drift()
    {
        var change = new PowerPlanChange(_plans, FakePowerPlans.HighId);
        Assert.Equal("powerplan", change.Kind);
        Assert.Equal("Energiesparplan", change.Target);
        Assert.Equal("Energiesparplan auf „Höchstleistung“ umstellen", change.Description);

        var record = change.Apply(Ctx());
        Assert.Equal(FakePowerPlans.BalancedId.ToString("D"), record.Before["plan"]);
        Assert.Equal(FakePowerPlans.HighId.ToString("D"), record.After["plan"]);
        Assert.Equal(FakePowerPlans.HighId, _plans.ActiveId);
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(record));

        _plans.ActiveId = FakePowerPlans.SaverId;
        Assert.Equal(DriftState.Changed, change.CheckDrift(record));

        change.Undo(record, Ctx());
        Assert.Equal(FakePowerPlans.BalancedId, _plans.ActiveId);
    }

    [Fact]
    public void RetryData_rebuilds_through_FromRecord()
    {
        var change = new PowerPlanChange(_plans, FakePowerPlans.SaverId);
        var (before, after) = change.RetryData()!.Value;
        Assert.Empty(before);
        var again = PowerPlanChange.FromRecord(
            ((WinWaechter.Core.Changes.IChange)change).Plan(Ctx()) with { Before = before, After = after }, _plans);
        Assert.Equal(change.Description, again.Description);
    }

    [Fact]
    public void FromRecord_rejects_unlisted()
    {
        var change = new PowerPlanChange(_plans, FakePowerPlans.HighId);
        var record = change.Apply(Ctx());

        var unknown = record with { After = new() { ["plan"] = Guid.NewGuid().ToString("D") } };
        Assert.Throws<InvalidOperationException>(() => PowerPlanChange.FromRecord(unknown, _plans));
        var garbage = record with { After = new() { ["plan"] = "not-a-guid" } };
        Assert.Throws<InvalidOperationException>(() => PowerPlanChange.FromRecord(garbage, _plans));

        // an old plan that is not a GUID or no longer installed is never activated
        var badBefore = record with { Before = new() { ["plan"] = "not-a-guid" } };
        var rebuilt = PowerPlanChange.FromRecord(badBefore, _plans);
        _plans.SetCalls.Clear();
        Assert.Throws<InvalidOperationException>(() => rebuilt.Undo(badBefore, Ctx()));
        var goneBefore = record with { Before = new() { ["plan"] = Guid.NewGuid().ToString("D") } };
        Assert.Throws<InvalidOperationException>(() => rebuilt.Undo(goneBefore, Ctx()));
        Assert.Empty(_plans.SetCalls);
    }

    [Fact]
    public void Target_must_be_existing_plan()
    {
        Assert.Throws<ArgumentException>(() => new PowerPlanChange(_plans, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new PowerPlanChange(_plans, Guid.Empty));
    }
}
