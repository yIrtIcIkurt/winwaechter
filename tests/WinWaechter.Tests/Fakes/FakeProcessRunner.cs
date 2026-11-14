using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Records invocations; a handler can delay or fail individual calls.
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string Exe, string Args)> Calls { get; } = [];
    public Func<string, string, Task<(int, string, string)>>? Handler { get; set; }

    /// <summary>The output encoding each call asked for (null = default); only calls through the encoding overload.</summary>
    public List<System.Text.Encoding?> Encodings { get; } = [];

    public Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct)
    {
        lock (Calls) Calls.Add((exe, args));
        return Handler?.Invoke(exe, args) ?? Task.FromResult((0, "", ""));
    }

    public async Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine,
        System.Text.Encoding? outputEncoding, CancellationToken ct)
    {
        lock (Calls) Encodings.Add(outputEncoding);
        var r = await RunAsync(exe, args, ct);
        if (onLine is not null)
            foreach (var line in ProcessRunner.SplitLines(r.StdOut)) onLine(line);
        return r;
    }
}
