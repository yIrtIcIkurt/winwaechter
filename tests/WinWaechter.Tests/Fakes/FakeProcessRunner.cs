using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Records invocations; a handler can delay or fail individual calls.
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string Exe, string Args)> Calls { get; } = [];
    public Func<string, string, Task<(int, string, string)>>? Handler { get; set; }

    public Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct)
    {
        lock (Calls) Calls.Add((exe, args));
        return Handler?.Invoke(exe, args) ?? Task.FromResult((0, "", ""));
    }
}
