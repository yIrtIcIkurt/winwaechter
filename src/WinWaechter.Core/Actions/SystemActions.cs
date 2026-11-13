using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Actions;

public interface IRestarter
{
    void Restart();
}

/// <summary>Restarts Windows immediately with the system's own shutdown.exe and fixed arguments.</summary>
public sealed class WinRestarter(IProcessRunner runner, string systemRoot) : IRestarter
{
    public const string Arguments = "/r /t 0";

    public void Restart() =>
        runner.RunAsync(Path.Combine(systemRoot, "System32", "shutdown.exe"), Arguments, CancellationToken.None)
            .GetAwaiter().GetResult();
}
