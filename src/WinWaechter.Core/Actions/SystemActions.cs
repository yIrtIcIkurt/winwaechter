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
    /// <summary>
    /// Restarts the Explorer shell so context-menu changes take effect at once. Ends only <c>explorer</c> processes
    /// whose image is exactly <c>&lt;windowsDir&gt;\explorer.exe</c>; if Windows has not brought one back after
    /// <paramref name="wait"/> (default 3 s), starts it with the signed-in user's normal rights.
    /// </summary>
    public static SystemAction RestartExplorer(IProcessEnder ender, Action<string> startAsUser, string windowsDir, TimeSpan? wait = null) =>
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

            await Task.Delay(wait ?? TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (Ours().Count == 0) startAsUser(exe);
            return new ActionOutcome(true, "Der Explorer wurde neu gestartet.");
        });
}
