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

    public void Restart()
    {
        var (exit, _, err) = runner.RunAsync(Path.Combine(systemRoot, "System32", "shutdown.exe"), Arguments, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (exit != 0)
            throw new InvalidOperationException($"Der Neustart ließ sich nicht auslösen (Code {exit}): {err.Trim()}");
    }
}

public static class SystemActions
{
    public const string ExplorerNotBack =
        "Der Explorer ist nicht von selbst zurückgekehrt. Starten Sie ihn mit Strg+Umschalt+Esc → „Neuen Task ausführen“ → explorer.exe.";

    /// <summary>
    /// Restarts the Explorer shell so context-menu changes take effect at once. Ends only <c>explorer</c> processes
    /// whose image is exactly the Windows folder's explorer.exe, then waits until Windows has started the shell again
    /// (polling every <paramref name="poll"/>, default 250 ms, up to <paramref name="timeout"/>, default 15 s).
    /// It never starts Explorer itself: the elevated app must not launch the user's shell.
    /// </summary>
    public static SystemAction RestartExplorer(IProcessEnder ender, string windowsDir, TimeSpan? poll = null, TimeSpan? timeout = null) =>
        new("restart-explorer", "Explorer neu starten", "Neu starten", null, false, async (p, ct) =>
        {
            var exe = ProcessGuard.NormalizePath(Path.Combine(windowsDir, "explorer.exe"))
                      ?? throw new InvalidOperationException("Der Windows-Ordner ist unbekannt.");
            IReadOnlyList<RunningProcess> Ours() => ender.Find("explorer")
                .Where(r => string.Equals(ProcessGuard.NormalizePath(r.ExePath), exe, StringComparison.OrdinalIgnoreCase))
                .ToList();

            p?.Report("Explorer wird neu gestartet …");
            var running = Ours();
            if (running.Count > 0 && ender.Kill(running) == 0)
                return new ActionOutcome(false, "Der Explorer ließ sich nicht beenden.");

            var step = poll ?? TimeSpan.FromMilliseconds(250);
            var left = timeout ?? TimeSpan.FromSeconds(15);
            while (true)
            {
                await Task.Delay(step, ct).ConfigureAwait(false);
                if (Ours().Count > 0) return new ActionOutcome(true, "Der Explorer wurde neu gestartet.");
                left -= step;
                if (left <= TimeSpan.Zero) return new ActionOutcome(false, ExplorerNotBack);
            }
        });
}
