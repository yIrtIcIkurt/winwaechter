using WinWaechter.Core.Programs;

namespace WinWaechter.Tests.Fakes;

// Records requested uninstall commands and returns a fixed exit code.
public sealed class FakeUninstallLauncher : IUninstallLauncher
{
    public List<UninstallCommand> Calls { get; } = [];
    public int ExitCode { get; set; }
    public bool ChildrenStillRunning { get; set; }

    public Task<UninstallOutcome> RunAsync(UninstallCommand cmd)
    {
        lock (Calls) Calls.Add(cmd);
        return Task.FromResult(UninstallExit.Interpret(ExitCode, ChildrenStillRunning));
    }
}
