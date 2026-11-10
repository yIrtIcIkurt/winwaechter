using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>Everything the Programme page does with installed programs.</summary>
public interface IProgramActions
{
    /// <summary>Visible programs (hidden entries left out).</summary>
    IReadOnlyList<InstalledProgram> ReadAll();
    bool IsStillListed(InstalledProgram p);
    UninstallPlan PlanUninstall(InstalledProgram p);
    SignatureInfo? UninstallerSignature(UninstallPlan plan);     // null when no command; never throws
    Task<UninstallOutcome> RunUninstallerAsync(UninstallPlan plan);   // InvalidOperationException unless Route == Launch
    IReadOnlyList<Finding> FindLeftovers(InstalledProgram removed);   // reads remaining entries itself
    ForcedRemovalPlan PlanForcedRemoval(InstalledProgram p);          // reads all entries itself
}

public sealed class ProgramService : IProgramActions
{
    readonly ProgramInventory _inventory;
    readonly IRegistry _reg;
    readonly IFileOps _files;
    readonly ISignatureVerifier _sig;
    readonly IUninstallLauncher _launcher;
    readonly UninstallLeftoverFinder _leftovers;
    readonly ForcedRemovalPlanner _forced;
    readonly CleanerPaths _paths;
    readonly IPathTrust _trust = new WinPathTrust();

    public ProgramService(ProgramInventory inventory, IRegistry reg, IFileOps files, ISignatureVerifier sig,
        IUninstallLauncher launcher, UninstallLeftoverFinder leftovers, ForcedRemovalPlanner forced, CleanerPaths paths)
    {
        _inventory = inventory; _reg = reg; _files = files; _sig = sig; _launcher = launcher;
        _leftovers = leftovers; _forced = forced; _paths = paths;
    }

    public IReadOnlyList<InstalledProgram> ReadAll() => _inventory.Read();

    public bool IsStillListed(InstalledProgram p) => _reg.KeyExists(p.Hive, p.RegistryKey);

    public UninstallPlan PlanUninstall(InstalledProgram p) => UninstallPlanner.Plan(p, _paths, _files.Exists, _trust);

    public SignatureInfo? UninstallerSignature(UninstallPlan plan)
    {
        if (plan.Command is not { } cmd) return null;
        try { return _sig.Verify(cmd.Exe); }
        catch (Exception ex)
        {
            FileLog.Warn($"Signatur des Deinstallationsprogramms nicht prüfbar: {cmd.Exe}", ex);
            return new SignatureInfo(SignatureState.Unknown, null, false);
        }
    }

    public Task<UninstallOutcome> RunUninstallerAsync(UninstallPlan plan)
    {
        if (plan.Route != UninstallRoute.Launch || plan.Command is not { } cmd)
            throw new InvalidOperationException("Dieses Deinstallationsprogramm startet WinWächter nicht selbst.");
        return _launcher.RunAsync(cmd);
    }

    // Hidden entries (components, updates) count as well: a folder one of them uses is never offered.
    public IReadOnlyList<Finding> FindLeftovers(InstalledProgram removed) =>
        _leftovers.Find(removed, _inventory.Read(includeHidden: true));

    public ForcedRemovalPlan PlanForcedRemoval(InstalledProgram p) => _forced.Plan(p, _inventory.Read(includeHidden: true));
}
