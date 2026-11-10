using WinWaechter.Core.Model;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Settable programs, plans and leftovers; records what the view model asked for. Never runs anything.
public sealed class FakeProgramActions : IProgramActions
{
    public List<InstalledProgram> Programs { get; set; } = [];
    public Func<InstalledProgram, UninstallPlan> PlanFor { get; set; } =
        _ => new UninstallPlan(UninstallRoute.OpenSettings, null, UninstallPlanner.UnsafeReason, false);
    public SignatureInfo? Signature { get; set; } = new(SignatureState.Valid, "Foo GmbH", false);
    public int ExitCode { get; set; }
    public bool ChildrenStillRunning { get; set; }
    public Func<InstalledProgram, bool> StillListed { get; set; } = _ => false;
    public List<Finding> Leftovers { get; set; } = [];
    public Func<InstalledProgram, ForcedRemovalPlan> ForcedFor { get; set; } = p => new(p, [], null, []);

    public int ReadCount { get; private set; }
    public List<InstalledProgram> Planned { get; } = [];
    public List<UninstallPlan> Runs { get; } = [];
    public List<InstalledProgram> LeftoverCalls { get; } = [];
    public List<InstalledProgram> ForcedCalls { get; } = [];
    public Exception? FindLeftoversThrows { get; set; }
    public Exception? RunThrows { get; set; }
    public int RunThreadId { get; private set; }

    public IReadOnlyList<InstalledProgram> ReadAll()
    {
        ReadCount++;
        return Programs.ToList();
    }

    public bool IsStillListed(InstalledProgram p) => StillListed(p);

    public UninstallPlan PlanUninstall(InstalledProgram p)
    {
        Planned.Add(p);
        return PlanFor(p);
    }

    public SignatureInfo? UninstallerSignature(UninstallPlan plan) => plan.Command is null ? null : Signature;

    public Task<UninstallOutcome> RunUninstallerAsync(UninstallPlan plan)
    {
        if (plan.Route is not (UninstallRoute.Launch or UninstallRoute.LaunchAsUser)) throw new InvalidOperationException("not a launch plan");
        Runs.Add(plan);
        if (RunThrows is { } ex) return Task.FromException<UninstallOutcome>(ex);
        RunThreadId = Environment.CurrentManagedThreadId;
        return Task.FromResult(UninstallExit.Interpret(ExitCode, ChildrenStillRunning));
    }

    public IReadOnlyList<Finding> FindLeftovers(InstalledProgram removed)
    {
        LeftoverCalls.Add(removed);
        if (FindLeftoversThrows is { } ex) throw ex;
        return Leftovers;
    }

    public ForcedRemovalPlan PlanForcedRemoval(InstalledProgram p)
    {
        ForcedCalls.Add(p);
        return ForcedFor(p);
    }
}
