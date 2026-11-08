using System.Diagnostics;

namespace WinWaechter.Core.Sys;

public interface IProcessRunner
{
    Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct);
}

// Runs a hidden process and collects its output. Cancelling only stops waiting; the process keeps running.
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
