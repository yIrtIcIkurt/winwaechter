using System.Diagnostics;
using System.Text;

namespace WinWaechter.Core.Sys;

public interface IProcessRunner
{
    Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct);

    /// <summary>Like <see cref="RunAsync(string,string,CancellationToken)"/>, and reports each output line
    /// (CR and LF both end a line, so redrawn progress arrives as separate lines). The default replays the
    /// lines after the program ended.</summary>
    async Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine, CancellationToken ct)
    {
        var r = await RunAsync(exe, args, ct).ConfigureAwait(false);
        if (onLine is not null)
            foreach (var line in ProcessRunner.SplitLines(r.StdOut)) onLine(line);
        return r;
    }

    /// <summary>Like the line overload, decoding the output with outputEncoding (null = the default, which in a
    /// window app is the ANSI code page; winget writes UTF-8).</summary>
    Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine, Encoding? outputEncoding,
        CancellationToken ct) => RunAsync(exe, args, onLine, ct);
}

// Runs a hidden process and collects its output. Cancelling only stops waiting; the process keeps running.
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Splits on CR, LF and CRLF; empty lines are dropped.</summary>
    public static IReadOnlyList<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    public Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct) =>
        RunAsync(exe, args, null, ct);

    public Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine, CancellationToken ct) =>
        RunAsync(exe, args, onLine, null, ct);

    public async Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine,
        Encoding? outputEncoding, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = ReadAsync(p.StandardOutput, onLine);
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    // Reads everything; reports each line as soon as a CR or LF ends it.
    static async Task<string> ReadAsync(StreamReader reader, Action<string>? onLine)
    {
        if (onLine is null) return await reader.ReadToEndAsync().ConfigureAwait(false);
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            all.Append(buffer, 0, n);
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0) onLine(line.ToString());
                    line.Clear();
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0) onLine(line.ToString());
        return all.ToString();
    }
}
