using WinWaechter.Core.Programs;

namespace WinWaechter.Tests.Fakes;

// Records requested uninstall commands and returns a fixed exit code.
public sealed class FakeUninstallLauncher : IUninstallLauncher
{
    public List<UninstallCommand> Calls { get; } = [];
    public List<string?> InstallLocations { get; } = [];
    public int ExitCode { get; set; }
    public bool ChildrenStillRunning { get; set; }

    public Task<UninstallOutcome> RunAsync(UninstallCommand cmd, string? installLocation = null)
    {
        lock (Calls)
        {
            Calls.Add(cmd);
            InstallLocations.Add(installLocation);
        }
        return Task.FromResult(UninstallExit.Interpret(ExitCode, ChildrenStillRunning));
    }
}
